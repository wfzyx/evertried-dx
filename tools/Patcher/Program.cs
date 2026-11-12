using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Patches Assembly-CSharp.dll so it calls into EvertriedQoL.dll.
//   Patcher install   <gameDir> <EvertriedQoL.dll> <backupDir>
//   Patcher uninstall <gameDir> <backupDir>
// The pristine Assembly-CSharp.dll is saved to <backupDir> on the first run and every install
// patches a fresh copy of it, so installs are idempotent.

static int Fail(string message)
{
    Console.Error.WriteLine("ERROR: " + message);
    return 1;
}

if (args.Length < 1)
{
    return Fail("usage: Patcher install <gameDir> <helper.dll> <backupDir> | uninstall <gameDir> <backupDir>");
}

string mode = args[0];
string gameDir = args[1];
string managed = Path.Combine(gameDir, "Evertried_Release_Data", "Managed");
string target = Path.Combine(managed, "Assembly-CSharp.dll");

if (mode == "uninstall")
{
    string backupDirU = args[2];
    string backupU = Path.Combine(backupDirU, "Assembly-CSharp.dll.orig");
    if (!File.Exists(backupU))
    {
        return Fail("no backup at " + backupU);
    }
    File.Copy(backupU, target, true);
    File.Delete(Path.Combine(managed, "EvertriedQoL.dll"));
    Console.WriteLine("Restored original Assembly-CSharp.dll");
    return 0;
}

if (mode != "install" || args.Length < 4)
{
    return Fail("bad arguments");
}

string helperPath = args[2];
string backupDir = args[3];
Directory.CreateDirectory(backupDir);
string backup = Path.Combine(backupDir, "Assembly-CSharp.dll.orig");

if (!File.Exists(backup))
{
    // Refuse to treat an already patched file as pristine.
    using (AssemblyDefinition probe = AssemblyDefinition.ReadAssembly(target))
    {
        if (probe.MainModule.AssemblyReferences.Any(r => r.Name == "EvertriedQoL"))
        {
            return Fail("Assembly-CSharp.dll is already patched and there is no pristine backup; verify game files in Steam first");
        }
    }
    File.Copy(target, backup);
    Console.WriteLine("Saved pristine backup: " + backup);
}

DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);

AssemblyDefinition helper = AssemblyDefinition.ReadAssembly(helperPath, new ReaderParameters { AssemblyResolver = resolver });

// 1. Every member the helper uses must exist in the game's (stripped) assemblies.
// Compiler-emitted custom attributes (CompilerGenerated, TargetFramework, ...) are stripped too, but
// they are only metadata: the runtime never needs them to run the code.
static bool IsAttributeMetadata(string fullName) => fullName.Contains("Attribute");

int missing = 0;
foreach (MemberReference member in helper.MainModule.GetMemberReferences())
{
    if (IsAttributeMetadata(member.DeclaringType.FullName))
    {
        continue;
    }
    bool ok;
    try
    {
        ok = member switch
        {
            MethodReference m => m.Resolve() != null,
            FieldReference f => f.Resolve() != null,
            _ => true,
        };
    }
    catch (Exception)
    {
        ok = false;
    }
    if (!ok)
    {
        missing++;
        Console.Error.WriteLine("MISSING in game assemblies: " + member.FullName);
    }
}
foreach (TypeReference type in helper.MainModule.GetTypeReferences())
{
    if (!IsAttributeMetadata(type.FullName) && type.Resolve() == null)
    {
        missing++;
        Console.Error.WriteLine("MISSING type in game assemblies: " + type.FullName);
    }
}
if (missing > 0)
{
    return Fail(missing + " member(s) used by the helper are stripped from the game");
}

// 2. Patch a fresh copy of the pristine assembly.
ReaderParameters readParams = new ReaderParameters { AssemblyResolver = resolver, ReadWrite = false };
AssemblyDefinition game = AssemblyDefinition.ReadAssembly(backup, readParams);
ModuleDefinition module = game.MainModule;

MethodReference Hook(string type, string name)
{
    MethodDefinition def = helper.MainModule.GetType("EvertriedQoL", type).Methods.Single(m => m.Name == name);
    return module.ImportReference(def);
}

MethodDefinition Find(string type, string name, int paramCount)
{
    TypeDefinition t = module.GetType(type) ?? throw new InvalidOperationException("type not found: " + type);
    return t.Methods.Single(m => m.Name == name && m.Parameters.Count == paramCount);
}

void InsertPrologue(MethodDefinition method, params Instruction[] code)
{
    method.Body.SimplifyMacros(); // long-form branches so inserted code cannot overflow short offsets
    ILProcessor il = method.Body.GetILProcessor();
    Instruction first = method.Body.Instructions[0];
    foreach (Instruction ins in code)
    {
        il.InsertBefore(first, ins);
    }
    // Branches that looped back to the old first instruction now run the hook too, which is harmless
    // for idempotent hooks but wrong for others; hooks used here are prologue-once-per-call safe.
}

void InsertBeforeEveryRet(MethodDefinition method, Func<Instruction[]> code)
{
    method.Body.SimplifyMacros();
    ILProcessor il = method.Body.GetILProcessor();
    foreach (Instruction ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
    {
        Instruction[] block = code();
        foreach (Instruction ins in block)
        {
            il.InsertBefore(ret, ins);
        }
        // Early-exit branches that targeted the ret must run the hook too.
        foreach (Instruction other in method.Body.Instructions)
        {
            if (other.Operand == ret)
            {
                other.Operand = block[0];
            }
            else if (other.Operand is Instruction[] targets)
            {
                for (int k = 0; k < targets.Length; k++)
                {
                    if (targets[k] == ret)
                    {
                        targets[k] = block[0];
                    }
                }
            }
        }
        // Exception handler ranges ending at ret are not expected: ret cannot be inside a protected region.
    }
}


// Prologue that answers the call from the helper when it is active and otherwise falls through to the
// original body: `if (Helper.Active()) { <replacement>; ret }`.
void InsertGuardedReplacement(MethodDefinition method, MethodReference active, params Instruction[] replacement)
{
    method.Body.SimplifyMacros();
    ILProcessor il = method.Body.GetILProcessor();
    Instruction first = method.Body.Instructions[0];
    il.InsertBefore(first, Instruction.Create(OpCodes.Call, active));
    il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
    foreach (Instruction ins in replacement)
    {
        il.InsertBefore(first, ins);
    }
    method.Body.OptimizeMacros();
}

// Every call to `fromFullName` anywhere in the game assembly now calls `to` instead (same signature).
int RetargetCalls(string fromFullName, MethodReference to)
{
    int count = 0;
    IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types) =>
        types.SelectMany(t => new[] { t }.Concat(Flatten(t.NestedTypes)));
    foreach (TypeDefinition type in Flatten(module.Types))
    {
        foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
        {
            foreach (Instruction ins in method.Body.Instructions)
            {
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                    && ins.Operand is MethodReference called && called.FullName == fromFullName)
                {
                    ins.OpCode = OpCodes.Call;
                    ins.Operand = to;
                    count++;
                }
            }
        }
    }
    return count;
}

// --- Mastery: scale each tree before any SkillTreeData member reads it.
MethodReference ensureScaled = Hook("MasteryScaler", "EnsureScaled");
TypeDefinition tree = module.GetType("SkillTreeData");
string[] skip = { "GrabValues", "SetMasteryTiers", "SetBaseMasteryEffects", "ParseEffects" };
int treeMethods = 0;
foreach (MethodDefinition method in tree.Methods.Where(m => m.HasBody && !m.IsStatic && !m.IsConstructor && !skip.Contains(m.Name)).ToList())
{
    InsertPrologue(method, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, ensureScaled));
    treeMethods++;
}
Console.WriteLine("Mastery hook: " + treeMethods + " SkillTreeData methods");

// --- Flawless: damage / level end / level start.
MethodDefinition applyDamage = Find("PlayerStatus", "ApplyDamage", 4);
MethodReference onDamage = Hook("Flawless", "OnPlayerDamage");
InsertBeforeEveryRet(applyDamage, () => new[]
{
    Instruction.Create(OpCodes.Dup),      // keep the bool result for the caller
    Instruction.Create(OpCodes.Ldarg_1),  // amount
    Instruction.Create(OpCodes.Ldarg_2),  // source
    Instruction.Create(OpCodes.Call, onDamage),
});

MethodDefinition endLevel = Find("LevelManager", "EndCurrentLevel", 1);
InsertPrologue(endLevel,
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldarg_1),
    Instruction.Create(OpCodes.Call, Hook("Flawless", "OnBeforeLevelEnd")));

MethodDefinition startLevel = Find("LevelManager", "StartLevel", 0);
MethodReference afterStart = Hook("Flawless", "OnAfterLevelStart");
InsertBeforeEveryRet(startLevel, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, afterStart),
});
Console.WriteLine("Flawless hooks installed");


// --- Controller input: answer the game's pad queries from evdev (see DxPad) instead of Unity's
// Windows-numbered joystick axes. Everything falls through to the original code when no pad is readable.
MethodReference padActive = Hook("DxPad", "Active");
MethodDefinition getControllerValue = module.GetType("ControllerInputs").Methods
    .Single(m => m.Name == "GetControllerValue" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Int32");
InsertGuardedReplacement(getControllerValue, padActive,
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldarg_1),
    Instruction.Create(OpCodes.Call, Hook("DxPad", "Button")),
    Instruction.Create(OpCodes.Ret));

MethodDefinition buttonIndex = Find("InputHandler", "GetControllerButtonIndex", 1);
InsertGuardedReplacement(buttonIndex, padActive,
    Instruction.Create(OpCodes.Ldarg_1), // semantic index = the ControllerKeyCode itself
    Instruction.Create(OpCodes.Ret));

int axisRaw = RetargetCalls("System.Single UnityEngine.Input::GetAxisRaw(System.String)", Hook("DxPad", "GetAxisRaw"));
int axis = RetargetCalls("System.Single UnityEngine.Input::GetAxis(System.String)", Hook("DxPad", "GetAxis"));
int anyKey = RetargetCalls("System.Boolean UnityEngine.Input::get_anyKeyDown()", Hook("DxPad", "AnyKeyDown"));
Console.WriteLine($"Controller hooks: 2 guarded methods, {axisRaw} GetAxisRaw, {axis} GetAxis, {anyKey} anyKeyDown call sites");

// --- Boot splash: skippable with a face button / Start (see DxSplash).
MethodDefinition splashStart = Find("SplashScreen", "Start", 0);
MethodReference splashBegin = Hook("DxSplash", "Begin");
InsertBeforeEveryRet(splashStart, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, splashBegin),
});

// --- Settings: the "Gamepad Type" entry is now only a glyph set picker (see DxText).
MethodDefinition getText = module.GetType("Localization.LocalizedTextData").Methods
    .Single(m => m.Name == "GetText" && m.Parameters.Count == 1);
MethodReference textFix = Hook("DxText", "Fix");
InsertBeforeEveryRet(getText, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldarg_1),
    Instruction.Create(OpCodes.Call, textFix),
});
Console.WriteLine("Splash skip and glyph set label hooks installed");

// --- Black world on Linux: skip the BilinearSharp upscale shader (see DxPixel).
MethodDefinition pixelStart = Find("PixelArtCamera", "Start", 0);
InsertBeforeEveryRet(pixelStart, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, Hook("DxPixel", "Fix")),
});

// --- Glyph set must not pick the d-pad axis names: with PS4 the game reads its horizontal d-pad from
// "VerticalDPad" (a Windows hat quirk), which DxPad cannot tell apart from the real vertical d-pad.
MethodDefinition axisType = Find("Axis", "OnControllerTypeChange", 1);
{
    axisType.Body.SimplifyMacros();
    ILProcessor il = axisType.Body.GetILProcessor();
    Instruction first = axisType.Body.Instructions[0];
    il.InsertBefore(first, Instruction.Create(OpCodes.Call, padActive));
    il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
    il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_0)); // ControllerType.XBOX
    il.InsertBefore(first, Instruction.Create(OpCodes.Starg, axisType.Parameters[0]));
    axisType.Body.OptimizeMacros();
}

// --- Title screen: no menu input until its fade-in has finished (see DxSplash).
MethodDefinition splashDispose = Find("SplashScreen", "Dispose", 0);
InsertPrologue(splashDispose, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, Hook("DxSplash", "OnDispose")));
MethodDefinition menuUpdate = Find("Evertried.MainMenu.MainMenu", "ActiveUpdate", 0);
{
    menuUpdate.Body.SimplifyMacros();
    ILProcessor il = menuUpdate.Body.GetILProcessor();
    Instruction first = menuUpdate.Body.Instructions[0];
    il.InsertBefore(first, Instruction.Create(OpCodes.Call, Hook("DxSplash", "InputBlocked")));
    il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
    il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    menuUpdate.Body.OptimizeMacros();
}

// --- Denial boss: its Mark follows the vulnerable cell (see DxBoss).
TypeDefinition smokeBoss = module.GetType("Boss_Smoke");
MethodDefinition markMethod = Find("EnemyBase", "ApplyMark", 1);
InsertPrologue(markMethod, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, Hook("DxBoss", "BeforeMark")));
InsertBeforeEveryRet(markMethod, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldarg_1),
    Instruction.Create(OpCodes.Call, Hook("DxBoss", "AfterMark")),
});
FieldDefinition vulnerableField = smokeBoss.Fields.Single(f => f.Name == "vulnerableCell");
MethodDefinition reassignSpot = smokeBoss.Methods.Single(m => m.Name == "ReassignVulnerableSpot" && m.Parameters.Count == 0);
InsertBeforeEveryRet(reassignSpot, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldfld, vulnerableField),
    Instruction.Create(OpCodes.Call, Hook("DxBoss", "SetVulnerable")),
});
MethodDefinition clearCells = smokeBoss.Methods.Single(m => m.Name == "ClearCellGroups" && m.Parameters.Count == 0);
InsertPrologue(clearCells, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, Hook("DxBoss", "CellsClearing")));

// --- New skill slot: a visible cue (see DxSlot).
MethodDefinition addSlot = Find("PlayerCombat", "AddSkillSlot", 0);
InsertBeforeEveryRet(addSlot, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Call, Hook("DxSlot", "Added")),
});

// --- Glyph set must not change how input is read: the game swaps A/B and X/Y for the Switch glyph set.
MethodDefinition switchInversion = Find("InputHandler", "PromptSwitchInversion", 1);
InsertGuardedReplacement(switchInversion, padActive, Instruction.Create(OpCodes.Ret));

// --- Ascend: starting a session happens once (see DxMenu).
MethodDefinition startSession = Find("Evertried.MainMenu.MainMenu", "StartSession", 2);
{
    startSession.Body.SimplifyMacros();
    ILProcessor il = startSession.Body.GetILProcessor();
    Instruction first = startSession.Body.Instructions[0];
    il.InsertBefore(first, Instruction.Create(OpCodes.Call, Hook("DxMenu", "TryBeginSession")));
    il.InsertBefore(first, Instruction.Create(OpCodes.Brtrue, first));
    il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    startSession.Body.OptimizeMacros();
}
// --- Leaving a run for the main menu: the fade must not accept input (pause and game-over panels).
InsertPrologue(Find("PausePanel", "ToMainMenu", 0), Instruction.Create(OpCodes.Call, Hook("DxMenu", "BeginLeave")));
InsertPrologue(Find("GameplayCanvas", "MainMenu", 0), Instruction.Create(OpCodes.Call, Hook("DxMenu", "BeginLeave")));
// Every panel (pause, game over, mastery picker, ...) reads input through PanelControllable.Update.
MethodDefinition panelUpdate = Find("PanelControllable", "Update", 0);
{
    panelUpdate.Body.SimplifyMacros();
    ILProcessor il = panelUpdate.Body.GetILProcessor();
    Instruction first = panelUpdate.Body.Instructions[0];
    il.InsertBefore(first, Instruction.Create(OpCodes.Call, Hook("DxMenu", "InputLocked")));
    il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
    il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
    panelUpdate.Body.OptimizeMacros();
}
InsertPrologue(Find("LevelManager", "Awake", 0), Instruction.Create(OpCodes.Call, Hook("DxMenu", "Reset")));
MethodDefinition mainMenuStart = Find("Evertried.MainMenu.MainMenu", "Start", 0);
InsertPrologue(mainMenuStart, Instruction.Create(OpCodes.Call, Hook("DxMenu", "Reset")));

// --- Disable Focus Timer (a Windows-build option): save key, meter, and settings row.
MethodDefinition gameDataKeys = Find("GameData", "GetKeys", 0);
int renamed = 0;
foreach (Instruction ins in gameDataKeys.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "GoldMultiplier"))
{
    ins.Operand = "FocusTimerDisabled";
    renamed++;
}
MethodDefinition gameDataValues = Find("GameData", "GetCurrentValues", 0);
Instruction blank = gameDataValues.Body.Instructions.First(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "");
blank.OpCode = OpCodes.Call; // the first "" is the array slot 1 that held the unused gold multiplier
blank.Operand = Hook("DxFocus", "Serialize");
if (renamed != 1)
{
    return Fail("expected one GoldMultiplier key in GameData.GetKeys, found " + renamed);
}
MethodDefinition gameDataLoad = Find("GameData", "LoadSavedData", 1);
InsertBeforeEveryRet(gameDataLoad, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_1),
    Instruction.Create(OpCodes.Ldc_I4_1),
    Instruction.Create(OpCodes.Ldelem_Ref),
    Instruction.Create(OpCodes.Call, Hook("DxFocus", "Load")),
});
MethodDefinition gameDataDefaults = Find("GameData", "SetDefaultValues", 0);
InsertPrologue(gameDataDefaults, Instruction.Create(OpCodes.Call, Hook("DxFocus", "Reset")));
MethodDefinition timerMultiplier = module.GetType("FocusMeter").Methods.Single(m => m.Name == "get_TimerMultiplier");
InsertBeforeEveryRet(timerMultiplier, () => new[] { Instruction.Create(OpCodes.Call, Hook("DxFocus", "Multiplier")) });

// --- Trance (Meditate mastery tier 2): the game tests "MeditateTurns >= 0", which is always true, so the
// focus protection applied whenever Meditate was merely selected. It is meant to apply while meditating.
MethodDefinition meditateTrance = module.GetType("PlayerCombat").Methods.Single(m => m.Name == "get_MeditateTrance");
{
    meditateTrance.Body.SimplifyMacros();
    List<Instruction> ins = meditateTrance.Body.Instructions.ToList();
    int fixedCount = 0;
    for (int i = 0; i + 2 < ins.Count; i++)
    {
        if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is MethodReference mr && mr.Name == "get_MeditateTurns"
            && ins[i + 1].OpCode == OpCodes.Ldc_I4 && (int)ins[i + 1].Operand == 0 && ins[i + 2].OpCode == OpCodes.Blt)
        {
            ins[i + 2].OpCode = OpCodes.Ble; // jump to "false" when turns <= 0
            fixedCount++;
        }
    }
    if (fixedCount != 1)
    {
        throw new InvalidOperationException("MeditateTrance pattern not found exactly once: " + fixedCount);
    }
    meditateTrance.Body.OptimizeMacros();
}

TypeDefinition settingsPanel = module.GetType("SettingsPanel");
FieldDefinition Field(string name) => settingsPanel.Fields.Single(f => f.Name == name);
MethodDefinition panelStart = settingsPanel.Methods.Single(m => m.Name == "Start" && m.Parameters.Count == 0);
InsertBeforeEveryRet(panelStart, () => new[]
{
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldfld, Field("controllerDropdown")),
    Instruction.Create(OpCodes.Ldarg_0),
    Instruction.Create(OpCodes.Ldfld, Field("movementDropdown")),
    Instruction.Create(OpCodes.Call, Hook("DxSettings", "Setup")),
});
MethodDefinition panelElements = settingsPanel.Methods.Single(m => m.Name == "get_Elements");
InsertBeforeEveryRet(panelElements, () => new[] { Instruction.Create(OpCodes.Call, Hook("DxSettings", "Elements")) });
MethodDefinition panelToggle = settingsPanel.Methods.Single(m => m.Name == "Toggle" && m.Parameters.Count == 1);
InsertPrologue(panelToggle, Instruction.Create(OpCodes.Call, Hook("DxSettings", "Refresh")));
Console.WriteLine("Switch inversion guard, one-shot Ascend and Disable Focus Timer hooks installed");

foreach (MethodDefinition m in new[] { applyDamage, endLevel, startLevel, splashStart, getText, gameDataLoad, gameDataDefaults, timerMultiplier, panelStart, panelElements, panelToggle, mainMenuStart, pixelStart, splashDispose, markMethod, reassignSpot, clearCells, addSlot }.Concat(tree.Methods.Where(x => x.HasBody)))
{
    m.Body.OptimizeMacros();
}

string tmp = target + ".patched.tmp";
game.Write(tmp);
game.Dispose();
File.Move(tmp, target, true);
File.Copy(helperPath, Path.Combine(managed, "EvertriedQoL.dll"), true);
Console.WriteLine("Patched " + target);
return 0;
