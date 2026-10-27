#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <sddl.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <deque>
#include <iomanip>
#include <locale>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#include "vendor/minhook/include/MinHook.h"

// Names and signatures were discovered in the live game using IL2CPP.Mcp.
// Metadata offsets are resolved anew on every game start, never saved as addresses.
namespace {
using Ptr = void*;
HMODULE game;
HMODULE observerModule;
#define API(ret, name, ...) using name##_t = ret(*)(__VA_ARGS__); name##_t name;
API(Ptr, il2cpp_domain_get)
API(Ptr*, il2cpp_domain_get_assemblies, Ptr, size_t*)
API(Ptr, il2cpp_assembly_get_image, Ptr)
API(const char*, il2cpp_image_get_name, Ptr)
API(Ptr, il2cpp_class_from_name, Ptr, const char*, const char*)
API(Ptr, il2cpp_class_get_parent, Ptr)
API(Ptr, il2cpp_class_get_field_from_name, Ptr, const char*)
API(size_t, il2cpp_field_get_offset, Ptr)
API(Ptr, il2cpp_field_get_type, Ptr)
API(void, il2cpp_field_static_get_value, Ptr, Ptr)
API(Ptr, il2cpp_class_get_method_from_name, Ptr, const char*, int)
API(Ptr, il2cpp_method_get_param, Ptr, uint32_t)
API(char*, il2cpp_type_get_name, Ptr)
API(void, il2cpp_free, Ptr)
API(Ptr, il2cpp_object_get_virtual_method, Ptr, Ptr)
API(Ptr, il2cpp_runtime_invoke, Ptr, Ptr, Ptr*, Ptr*)
API(Ptr, il2cpp_object_unbox, Ptr)
API(Ptr, il2cpp_thread_attach, Ptr)
API(void, il2cpp_thread_detach, Ptr)
API(int32_t, il2cpp_string_length, Ptr)
API(const wchar_t*, il2cpp_string_chars, Ptr)
#undef API

template<class T> T Read(Ptr obj, size_t offset) {
    T value{};
    if (obj) std::memcpy(&value, static_cast<char*>(obj) + offset, sizeof(T));
    return value;
}
std::string Quote(const std::string& s) {
    std::ostringstream o; o << '"';
    for (unsigned char c : s) {
        switch (c) {
        case '"': o << "\\\""; break;
        case '\\': o << "\\\\"; break;
        case '\n': o << "\\n"; break;
        case '\r': o << "\\r"; break;
        case '\t': o << "\\t"; break;
        default:
            if (c < 32) o << "\\u00" << std::hex << std::setw(2) << std::setfill('0') << int(c) << std::dec;
            else o << c;
        }
    }
    o << '"'; return o.str();
}
std::string Text(Ptr str) {
    if (!str) return "";
    int n = il2cpp_string_length(str);
    if (n <= 0 || n > 16384) return "";
    const auto chars = il2cpp_string_chars(str);
    int size = WideCharToMultiByte(CP_UTF8, 0, chars, n, nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, chars, n, result.data(), size, nullptr, nullptr);
    return result;
}
double Number(float n) { return std::isfinite(n) ? n : 0; }
Ptr image, unitClass, heroClass, monsterClass, stageClass, spawnClass, healthClass;
Ptr nameMethod, cacheMethod, statMethod, stageInstanceField, spawnInstanceField;
Ptr attackMethod, armorMethod, speedMethod, critMethod, critDamageMethod, moveMethod, resistanceMethod;
size_t heroProtectedOffset;
size_t healthOffset, heroFlagOffset, heroCacheOffset, monsterCacheOffset, statsOffset;
size_t hpOffset, maxHpOffset, updateOffset, heroArrayOffset, monsterListOffset, summonListOffset;
size_t stateOffset, runStateOffset, stageCacheOffset, stageInfoOffset;
size_t stageKeyOffset, actOffset, stageNoOffset, stageLevelOffset;
size_t unitStateOffset, cacheInfoOffset, heroKeyOffset, levelOffset;
size_t dAttacker, dOrigin, dCrit, dElement, dType, dSource, dBasic, dRedistributed;
std::atomic<ULONGLONG> heartbeat{0};
std::atomic<bool> paused{false};
bool Capturing() { return !paused && GetTickCount64() - heartbeat.load() < 5000; }
std::mutex gate;
std::string snapshot = "\"ready\":false,\"message\":\"Waiting for game update\",\"heroes\":[],\"enemies\":[]";
std::string error;
uint64_t sequence = 0;
struct Event { uint64_t sequence; std::string json; };
std::deque<Event> events;
constexpr size_t EventCapacity = 32768;
std::atomic<uint64_t> errors{0};

Ptr Class(const char* ns, const char* name) {
    Ptr c = il2cpp_class_from_name(image, ns, name);
    if (!c) throw std::runtime_error(std::string("Class not found: ") + ns + "." + name);
    return c;
}
Ptr Field(Ptr c, const char* name) {
    for (; c; c = il2cpp_class_get_parent(c)) {
        Ptr f = il2cpp_class_get_field_from_name(c, name);
        if (f) return f;
    }
    throw std::runtime_error(std::string("Field not found: ") + name);
}
size_t Offset(Ptr c, const char* name) { return il2cpp_field_get_offset(Field(c, name)); }
Ptr Method(Ptr c, const char* name, int count) {
    Ptr m = il2cpp_class_get_method_from_name(c, name, count);
    if (!m) throw std::runtime_error(std::string("Method not found: ") + name);
    return m;
}
void CheckParam(Ptr m, uint32_t index, const char* expected) {
    char* t = il2cpp_type_get_name(il2cpp_method_get_param(m, index));
    bool ok = t && std::string(t) == expected;
    if (t) il2cpp_free(t);
    if (!ok) throw std::runtime_error("Game version is incompatible: method signature changed");
}
Ptr Invoke(Ptr method, Ptr instance, Ptr* args = nullptr, bool virt = false) {
    if (virt && instance) method = il2cpp_object_get_virtual_method(instance, method);
    Ptr ex = nullptr;
    Ptr result = il2cpp_runtime_invoke(method, instance, args, &ex);
    if (ex) throw std::runtime_error("Game getter raised an exception");
    return result;
}
std::string Id(Ptr obj) { return std::to_string(reinterpret_cast<uintptr_t>(obj)); }
std::string Name(Ptr obj) {
    return obj ? Text(Invoke(nameMethod, obj, nullptr, true)) : "Environment";
}
bool IsHero(Ptr obj) { return Read<bool>(obj, heroFlagOffset); }
float Hp(Ptr unit) { return Read<float>(Read<Ptr>(unit, healthOffset), hpOffset); }
Ptr Singleton(Ptr f) { Ptr obj = nullptr; il2cpp_field_static_get_value(f, &obj); return obj; }
float Getter(Ptr obj, Ptr method, Ptr* args = nullptr) {
    Ptr boxed = Invoke(method, obj, args);
    return boxed ? *static_cast<float*>(il2cpp_object_unbox(boxed)) : 0;
}
std::vector<Ptr> Array(Ptr array) {
    if (!array) return {};
    size_t n = Read<size_t>(array, 24);
    if (n > 4096) throw std::runtime_error("Invalid game collection size");
    std::vector<Ptr> result;
    for (size_t i = 0; i < n; ++i) {
        Ptr p = Read<Ptr>(array, 32 + i * sizeof(Ptr));
        if (p) result.push_back(p);
    }
    return result;
}
std::vector<Ptr> List(Ptr list) {
    if (!list) return {};
    Ptr a = Read<Ptr>(list, 16);
    int n = Read<int>(list, 24);
    if (n < 0 || n > 4096 || (a && size_t(n) > Read<size_t>(a, 24)))
        throw std::runtime_error("Invalid game list size");
    std::vector<Ptr> result;
    for (int i = 0; i < n; ++i) {
        Ptr p = Read<Ptr>(a, 32 + i * sizeof(Ptr));
        if (p) result.push_back(p);
    }
    return result;
}
std::string UnitJson(Ptr obj) {
    Ptr health = Read<Ptr>(obj, healthOffset);
    Ptr cache = Invoke(cacheMethod, obj, nullptr, true);
    Ptr stat = Read<Ptr>(cache, statsOffset);
    Ptr info = IsHero(obj) ? Read<Ptr>(cache, cacheInfoOffset) : nullptr;
    std::ostringstream o; o.imbue(std::locale::classic());
    o << "{\"id\":" << Quote(Id(obj)) << ",\"name\":" << Quote(Name(obj))
      << ",\"isHero\":" << (IsHero(obj) ? "true" : "false")
      << ",\"hp\":" << Number(Read<float>(health, hpOffset))
      << ",\"maxHp\":" << Number(Read<float>(health, maxHpOffset))
      << ",\"state\":" << Read<int>(obj, unitStateOffset)
      << ",\"heroKey\":" << Read<int>(info, heroKeyOffset)
      << ",\"attackDamage\":" << Number(Getter(obj, attackMethod))
      << ",\"armor\":" << Number(Getter(obj, armorMethod))
      << ",\"attackSpeed\":" << Number(Getter(obj, speedMethod))
      << ",\"criticalChance\":" << Number(Getter(obj, critMethod))
      << ",\"criticalDamage\":" << Number(Getter(obj, critDamageMethod))
      << ",\"movementSpeed\":" << Number(Getter(obj, moveMethod))
      << ",\"resistances\":[";
    for (int element = 1; element <= 4; ++element) {
        Ptr args[] = { &element };
        if (element > 1) o << ',';
        o << Number(Getter(obj, resistanceMethod, args));
    }
    o << ']'
      << ",\"stats\":[";
    for (int i = 1; i <= 81; ++i) {
        float value = 0;
        if (stat) {
            Ptr args[] = { &i };
            Ptr boxed = Invoke(statMethod, stat, args);
            if (boxed) value = *static_cast<float*>(il2cpp_object_unbox(boxed));
        }
        if (i > 1) o << ',';
        o << Number(value);
    }
    o << "]}"; return o.str();
}
void Capture(Ptr stage) {
    std::ostringstream o; o.imbue(std::locale::classic());
    Ptr stageCache = Read<Ptr>(stage, stageCacheOffset);
    Ptr info = Read<Ptr>(stageCache, stageInfoOffset);
    o << "\"ready\":true,\"stageKey\":" << Read<int>(info, stageKeyOffset)
      << ",\"act\":" << Read<int>(info, actOffset)
      << ",\"stageNo\":" << Read<int>(info, stageNoOffset)
      << ",\"stageLevel\":" << Read<int>(info, stageLevelOffset)
      << ",\"stageState\":" << Read<int>(stage, stateOffset)
      << ",\"runState\":" << Read<int>(stage, runStateOffset)
      << ",\"sampleTimeMs\":" << GetTickCount64() << ",\"heroes\":[";
    bool first = true;
    for (Ptr h : Array(Read<Ptr>(stage, heroArrayOffset))) {
        if (!first) o << ','; first = false; o << UnitJson(h);
    }
    o << "],\"enemies\":["; first = true;
    Ptr spawn = Singleton(spawnInstanceField);
    auto monsters = List(Read<Ptr>(spawn, monsterListOffset));
    auto summons = List(Read<Ptr>(spawn, summonListOffset));
    monsters.insert(monsters.end(), summons.begin(), summons.end());
    std::sort(monsters.begin(), monsters.end());
    monsters.erase(std::unique(monsters.begin(), monsters.end()), monsters.end());
    for (Ptr enemy : monsters) {
        if (Hp(enemy) <= 0 || !Read<bool>(enemy, updateOffset)) continue;
        if (!first) o << ','; first = false; o << UnitJson(enemy);
    }
    o << ']';
    std::lock_guard lock(gate); snapshot = o.str();
}
bool TryCapture(Ptr stage) {
    __try { Capture(stage); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
using UpdateFn = void(*)(Ptr, Ptr);
using HitFn = void(*)(Ptr, Ptr, bool, Ptr);
using HealthFn = void(*)(Ptr, float, Ptr, Ptr);
using DeathFn = bool(*)(Ptr, Ptr, Ptr);
UpdateFn originalUpdate;
HitFn originalHit;
HealthFn originalHealth;
HealthFn originalHeroHealth;
DeathFn originalHeroDeath;
struct Context { Ptr target, info; Context* previous; };
thread_local Context* context = nullptr;
struct HealthContext { Ptr target; uint64_t hitSequence; HealthContext* previous; };
thread_local HealthContext* healthContext = nullptr;
uint64_t Record(Ptr target, Ptr attacker, Ptr info, float damage, float hpLost, float healing,
    float hpBefore = 0, bool death = false, uint64_t fatalHit = 0, uint64_t boundary = 0, ULONGLONG eventTime = 0) {
    std::ostringstream o; o.imbue(std::locale::classic());
    const auto time = eventTime ? eventTime : GetTickCount64();
    o << "{\"timeMs\":" << time << ",\"attackerId\":" << Quote(Id(attacker))
      << ",\"targetId\":" << Quote(Id(target))
      << ",\"attacker\":" << Quote(Name(attacker)) << ",\"target\":" << Quote(Name(target))
      << ",\"attackerHero\":" << (IsHero(attacker) ? "true" : "false")
      << ",\"targetHero\":" << (IsHero(target) ? "true" : "false")
      << ",\"damage\":" << Number(damage) << ",\"hpLost\":" << Number(hpLost)
      << ",\"healing\":" << Number(healing)
      << ",\"hpBefore\":" << Number(hpBefore)
      << ",\"hpAfter\":" << Number(std::max(0.0f, hpBefore - hpLost + healing))
      << ",\"death\":" << (death ? "true" : "false")
      << ",\"fatalHitSequence\":" << fatalHit
      << ",\"deathBoundarySequence\":" << boundary
      << ",\"originDamage\":" << Number(info ? Read<float>(info, dOrigin) : damage)
      << ",\"critical\":" << ((info && Read<bool>(info, dCrit)) ? "true" : "false")
      << ",\"element\":" << (info ? Read<int>(info, dElement) : -1)
      << ",\"damageType\":" << (info ? Read<int>(info, dType) : 0)
      << ",\"source\":" << (info ? Read<int>(info, dSource) : -1)
      << ",\"basicAttack\":" << ((info && Read<bool>(info, dBasic)) ? "true" : "false")
      << ",\"redistributed\":" << ((info && Read<bool>(info, dRedistributed)) ? "true" : "false");
    std::lock_guard lock(gate);
    const auto seq = ++sequence; o << ",\"sequence\":" << seq << '}';
    events.push_back({seq, o.str()});
    if (events.size() > EventCapacity) events.pop_front();
    return seq;
}
uint64_t RecordCaught(Ptr target, Ptr attacker, Ptr info, float damage, float hpLost, float healing,
    float hpBefore, bool death, uint64_t fatalHit, uint64_t boundary, ULONGLONG eventTime) {
    try { return Record(target, attacker, info, damage, hpLost, healing, hpBefore, death, fatalHit, boundary, eventTime); }
    catch (...) { ++errors; return 0; }
}
uint64_t RecordSafe(Ptr target, Ptr attacker, Ptr info, float damage, float hpLost, float healing,
    float hpBefore = 0, bool death = false, uint64_t fatalHit = 0, uint64_t boundary = 0, ULONGLONG eventTime = 0) {
    __try { return RecordCaught(target, attacker, info, damage, hpLost, healing, hpBefore, death, fatalHit, boundary, eventTime); }
    __except(EXCEPTION_EXECUTE_HANDLER) { ++errors; return 0; }
}
void HitHook(Ptr self, Ptr info, bool flag, Ptr method) {
    if (!Capturing()) { originalHit(self, info, flag, method); return; }
    Context current{ self, info, context };
    context = &current;
    // Always restore the nested damage context, including on an IL2CPP exception.
    __try { originalHit(self, info, flag, method); }
    __finally { context = current.previous; }
}
void ObserveHealth(HealthFn original, Ptr self, float delta, Ptr attacker, Ptr method) {
    Ptr owner = Read<Ptr>(self, 48); // pp.bgaw, verified below during initialization.
    float before = Read<float>(self, hpOffset);
    bool watch = Capturing() && owner && Read<bool>(owner, updateOffset);
    if (watch && IsHero(owner) && Read<bool>(owner, heroProtectedOffset)) watch = false;
    Context* current = context;
    Ptr info = (current && current->target == owner) ? current->info : nullptr;
    // Record the game-calculated, post-mitigation amount before death/respawn callbacks.
    // Effective HP loss is capped at pre-hit HP; the remainder is overkill.
    uint64_t hitSequence = 0;
    if (watch && delta < 0 && before > 0) {
        hitSequence = RecordSafe(owner, info ? Read<Ptr>(info, dAttacker) : attacker, info,
            -delta, std::min(before, -delta), 0, before);
    }
    HealthContext frame{ owner, hitSequence, healthContext };
    healthContext = &frame;
    __try { original(self, delta, attacker, method); }
    __finally { healthContext = frame.previous; }
    if (watch && delta > 0) {
        float healed = std::max(0.0f, Read<float>(self, hpOffset) - before);
        if (healed > 0) RecordSafe(owner, attacker, nullptr, 0, 0, healed, before);
    }
}
void HealthHook(Ptr self, float delta, Ptr attacker, Ptr method) { ObserveHealth(originalHealth, self, delta, attacker, method); }
void HeroHealthHook(Ptr self, float delta, Ptr attacker, Ptr method) { ObserveHealth(originalHeroHealth, self, delta, attacker, method); }
uint64_t CurrentSequence() { std::lock_guard lock(gate); return sequence; }
bool HeroDeathHook(Ptr self, Ptr killer, Ptr method) {
    bool watch = Capturing();
    auto time = GetTickCount64();
    auto boundary = watch ? CurrentSequence() : 0;
    // hbd returns false when a death-prevention effect intercepted the lethal hit.
    bool died = originalHeroDeath(self, killer, method);
    if (watch && died) {
        Ptr info = (context && context->target == self) ? context->info : nullptr;
        uint64_t fatalHit = (healthContext && healthContext->target == self) ? healthContext->hitSequence : 0;
        RecordSafe(self, killer, info, 0, 0, 0, 0, true, fatalHit, boundary, time);
    }
    return died;
}
void UpdateHook(Ptr self, Ptr method) {
    originalUpdate(self, method);
    static ULONGLONG last = 0;
    const auto now = GetTickCount64();
    if (!Capturing() || now - last < 250) return;
    last = now;
    try {
        if (!TryCapture(self)) {
            std::lock_guard lock(gate); ++errors;
            error = "Could not read game state safely";
        } else { std::lock_guard lock(gate); error.clear(); }
    } catch (const std::exception& e) {
        std::lock_guard lock(gate); ++errors; error = e.what();
    } catch (...) { std::lock_guard lock(gate); ++errors; error = "Unknown capture error"; }
}
void Initialize() {
    game = GetModuleHandleW(L"GameAssembly.dll");
    if (!game) throw std::runtime_error("GameAssembly.dll is not loaded");
#define LOAD(name) name = reinterpret_cast<name##_t>(GetProcAddress(game, #name)); if (!name) throw std::runtime_error("Missing IL2CPP export: " #name);
    LOAD(il2cpp_domain_get) LOAD(il2cpp_domain_get_assemblies) LOAD(il2cpp_assembly_get_image)
    LOAD(il2cpp_image_get_name) LOAD(il2cpp_class_from_name) LOAD(il2cpp_class_get_parent)
    LOAD(il2cpp_class_get_field_from_name) LOAD(il2cpp_field_get_offset) LOAD(il2cpp_field_get_type)
    LOAD(il2cpp_field_static_get_value) LOAD(il2cpp_class_get_method_from_name)
    LOAD(il2cpp_method_get_param) LOAD(il2cpp_type_get_name) LOAD(il2cpp_free)
    LOAD(il2cpp_object_get_virtual_method) LOAD(il2cpp_runtime_invoke) LOAD(il2cpp_object_unbox)
    LOAD(il2cpp_thread_attach) LOAD(il2cpp_thread_detach) LOAD(il2cpp_string_length) LOAD(il2cpp_string_chars)
#undef LOAD
    Ptr attached = il2cpp_thread_attach(il2cpp_domain_get());
    size_t count = 0;
    Ptr* assemblies = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &count);
    for (size_t i = 0; i < count; ++i) {
        Ptr candidate = il2cpp_assembly_get_image(assemblies[i]);
        if (std::string(il2cpp_image_get_name(candidate)) == "Assembly-CSharp.dll") { image = candidate; break; }
    }
    if (!image) throw std::runtime_error("Assembly-CSharp.dll not found");
    unitClass = Class("TaskbarHero", "Unit"); heroClass = Class("TaskbarHero", "Hero");
    monsterClass = Class("TaskbarHero", "Monster"); stageClass = Class("TaskbarHero", "StageManager");
    spawnClass = Class("TaskbarHero.Manager", "MonsterSpawnManager"); healthClass = Class("", "pp");
    auto cacheClass = Class("", "xz"); auto statClass = Class("", "bcp");
    healthOffset = Offset(unitClass, "UnitHealthController");
    heroFlagOffset = Offset(unitClass, "b_isHero"); updateOffset = Offset(unitClass, "b_updateUnit");
    unitStateOffset = Offset(unitClass, "state");
    hpOffset = Offset(healthClass, "bgaz"); maxHpOffset = Offset(healthClass, "bgbd");
    heroProtectedOffset = Offset(heroClass, "bfxg");
    if (Offset(healthClass, "bgaw") != 48) throw std::runtime_error("Health layout changed");
    statsOffset = Offset(cacheClass, "<bjfr>k__BackingField");
    cacheInfoOffset = Offset(Class("", "xs"), "bjct");
    heroKeyOffset = Offset(Class("TaskbarHero.Data", "HeroInfoData"), "HeroKey");
    heroArrayOffset = Offset(stageClass, "HeroList");
    monsterListOffset = Offset(spawnClass, "MonsterList");
    summonListOffset = Offset(spawnClass, "SummonedMonsterList");
    stateOffset = Offset(stageClass, "stageState"); runStateOffset = Offset(stageClass, "_stageRunState");
    stageCacheOffset = Offset(stageClass, "bgvz");
    // StageCache is a nested class in xo; locate it from the field's type instead of a guessed namespace.
    auto classFromType = reinterpret_cast<Ptr(*)(Ptr)>(GetProcAddress(game, "il2cpp_class_from_type"));
    if (!classFromType) throw std::runtime_error("il2cpp_class_from_type is missing");
    Ptr sc = classFromType(il2cpp_field_get_type(Field(stageClass, "bgvz")));
    stageInfoOffset = Offset(sc, "bizr");
    auto si = Class("TaskbarHero.Data", "StageInfoData");
    stageKeyOffset = Offset(si, "StageKey"); actOffset = Offset(si, "Act");
    stageNoOffset = Offset(si, "StageNo"); stageLevelOffset = Offset(si, "StageLevel");
    stageInstanceField = Field(il2cpp_class_get_parent(stageClass), "bfok");
    spawnInstanceField = Field(il2cpp_class_get_parent(spawnClass), "bfok");
    nameMethod = Method(unitClass, "gyk", 0); cacheMethod = Method(unitClass, "gzg", 0);
    statMethod = Method(statClass, "mpj", 1);
    attackMethod = Method(unitClass, "hdw", 0); armorMethod = Method(unitClass, "hdx", 0);
    speedMethod = Method(unitClass, "gyo", 0); critMethod = Method(unitClass, "hea", 0);
    critDamageMethod = Method(unitClass, "heb", 0); moveMethod = Method(unitClass, "hdy", 0);
    resistanceMethod = Method(unitClass, "hew", 1);
    CheckParam(resistanceMethod, 0, "TaskbarHero.Data.EDamageAttribute");
    Ptr d = Class("TaskbarHero", "DamageInfo");
    auto off = [&](const char* f) { auto n = Offset(d, f); if (n < 16 || n > 64) throw std::runtime_error("Damage layout changed"); return n - 16; };
    dAttacker = off("Attacker"); dOrigin = off("OriginDamage"); dCrit = off("IsCritical");
    dElement = off("DamageAttribute"); dType = off("DamageType"); dSource = off("DamageSource");
    dBasic = off("IsBaseAttackDamage"); dRedistributed = off("IsRedistributedDamage");
    Ptr hit = Method(unitClass, "hal", 2); CheckParam(hit, 0, "TaskbarHero.DamageInfo"); CheckParam(hit, 1, "System.Boolean");
    Ptr hp = Method(healthClass, "hby", 2); CheckParam(hp, 0, "System.Single"); CheckParam(hp, 1, "TaskbarHero.Unit");
    Ptr heroHp = Method(Class("", "pl"), "hby", 2); CheckParam(heroHp, 0, "System.Single"); CheckParam(heroHp, 1, "TaskbarHero.Unit");
    Ptr heroDeath = Method(heroClass, "hbd", 1); CheckParam(heroDeath, 0, "TaskbarHero.Unit");
    auto returnType = reinterpret_cast<Ptr(*)(Ptr)>(GetProcAddress(game, "il2cpp_method_get_return_type"));
    if (!returnType) throw std::runtime_error("il2cpp_method_get_return_type is missing");
    char* deathResult = il2cpp_type_get_name(returnType(heroDeath));
    bool deathSignatureOk = deathResult && std::string(deathResult) == "System.Boolean";
    if (deathResult) il2cpp_free(deathResult);
    if (!deathSignatureOk) throw std::runtime_error("Hero death method signature changed");
    il2cpp_thread_detach(attached);
    auto ok = [](MH_STATUS s) { if (s != MH_OK) throw std::runtime_error(MH_StatusToString(s)); };
    ok(MH_Initialize());
    ok(MH_CreateHook(*static_cast<Ptr*>(hit), reinterpret_cast<Ptr>(HitHook), reinterpret_cast<Ptr*>(&originalHit)));
    ok(MH_CreateHook(*static_cast<Ptr*>(hp), reinterpret_cast<Ptr>(HealthHook), reinterpret_cast<Ptr*>(&originalHealth)));
    ok(MH_CreateHook(*static_cast<Ptr*>(heroHp), reinterpret_cast<Ptr>(HeroHealthHook), reinterpret_cast<Ptr*>(&originalHeroHealth)));
    ok(MH_CreateHook(*static_cast<Ptr*>(heroDeath), reinterpret_cast<Ptr>(HeroDeathHook), reinterpret_cast<Ptr*>(&originalHeroDeath)));
    ok(MH_CreateHook(*static_cast<Ptr*>(Method(stageClass, "Update", 0)), reinterpret_cast<Ptr>(UpdateHook), reinterpret_cast<Ptr*>(&originalUpdate)));
    // Queue only our own hooks; never enable hooks belonging to another library user.
    ok(MH_QueueEnableHook(*static_cast<Ptr*>(hit)));
    ok(MH_QueueEnableHook(*static_cast<Ptr*>(hp)));
    ok(MH_QueueEnableHook(*static_cast<Ptr*>(heroHp)));
    ok(MH_QueueEnableHook(*static_cast<Ptr*>(heroDeath)));
    ok(MH_QueueEnableHook(*static_cast<Ptr*>(Method(stageClass, "Update", 0))));
    ok(MH_ApplyQueued());
}
std::string Reply(uint64_t since) {
    std::lock_guard lock(gate);
    std::ostringstream o; o.imbue(std::locale::classic());
    uint64_t dropped = events.empty() || since >= events.front().sequence ? 0 : events.front().sequence - since - 1;
    o << "{\"protocol\":1,\"pid\":" << GetCurrentProcessId() << ",\"timeMs\":" << GetTickCount64()
      << ",\"lastSequence\":" << sequence << ",\"dropped\":" << dropped
      << ",\"captureErrors\":" << errors.load() << ",\"error\":" << Quote(error) << ',' << snapshot << ",\"events\":[";
    bool first = true;
    for (const auto& e : events) if (e.sequence > since) { if (!first) o << ','; first = false; o << e.json; }
    o << "]}"; return o.str();
}
PSECURITY_DESCRIPTOR UserSecurity() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return nullptr;
    DWORD n = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &n);
    std::vector<char> b(n);
    if (!GetTokenInformation(token, TokenUser, b.data(), n, &n)) { CloseHandle(token); return nullptr; }
    CloseHandle(token);
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(b.data())->User.Sid, &sid)) return nullptr;
    std::wstring sddl = L"D:P(A;;GA;;;" + std::wstring(sid) + L")"; LocalFree(sid);
    PSECURITY_DESCRIPTOR sd = nullptr;
    ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr);
    return sd;
}
bool WriteAll(HANDLE pipe, const void* data, DWORD n) {
    auto p = static_cast<const char*>(data);
    while (n) { DWORD done = 0; if (!WriteFile(pipe, p, n, &done, nullptr) || !done) return false; p += done; n -= done; }
    return true;
}
DWORD WINAPI Serve(Ptr) {
    try { Initialize(); }
    catch (const std::exception& e) { std::lock_guard lock(gate); error = e.what(); MH_Uninitialize(); }
    auto sd = UserSecurity();
    if (!sd) return 1; // Never expose a game pipe with a permissive default ACL.
    SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
    wchar_t modulePath[32768]{};
    GetModuleFileNameW(observerModule, modulePath, 32768);
    std::wstring moduleName = modulePath;
    moduleName = moduleName.substr(moduleName.find_last_of(L"\\/") + 1);
    auto dot = moduleName.find_last_of(L'.');
    std::wstring buildId = moduleName.substr(0, dot);
    std::wstring name = L"\\\\.\\pipe\\tbh-build-observer-" + std::to_wstring(GetCurrentProcessId()) + L"-" + buildId;
    while (true) {
        HANDLE pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1, 65536, 65536, 1000, &sa);
        if (pipe == INVALID_HANDLE_VALUE) break;
        if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            char request[128]{}; DWORD n = 0;
            if (ReadFile(pipe, request, sizeof(request) - 1, &n, nullptr)) {
                request[n] = 0;
                uint64_t since = 0;
                if (sscanf_s(request, "snapshot %llu", &since) == 1) {
                    heartbeat = GetTickCount64(); paused = false;
                } else if (std::string(request).find("pause") == 0) { paused = true; }
                auto reply = Reply(since);
                uint32_t size = static_cast<uint32_t>(reply.size());
                if (WriteAll(pipe, &size, sizeof(size)) && WriteAll(pipe, reply.data(), size)) FlushFileBuffers(pipe);
            }
        }
        DisconnectNamedPipe(pipe); CloseHandle(pipe);
    }
    LocalFree(sd); return 0;
}
}
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        observerModule = module;
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(nullptr, 0, Serve, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
