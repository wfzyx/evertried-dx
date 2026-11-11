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
MethodReference splashInBetween = Hook("DxSplash", "InBetween");
MethodDefinition endSequence = module.GetType("SplashScreen").NestedTypes
    .Single(t => t.Name.Contains("EndSequence_Routine")).Methods.Single(m => m.Name == "MoveNext" && m.HasBody);
int waits = 0;
{
    ILProcessor il = endSequence.Body.GetILProcessor();
    foreach (Instruction ins in endSequence.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == "inBetweenDuration").ToList())
    {
        il.InsertAfter(ins, Instruction.Create(OpCodes.Call, splashInBetween));
        waits++;
    }
}
if (waits != 1)
{
    return Fail("expected exactly one read of SplashScreen.inBetweenDuration, found " + waits);
}

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

foreach (MethodDefinition m in new[] { applyDamage, endLevel, startLevel, splashStart, getText }.Concat(tree.Methods.Where(x => x.HasBody)))
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
