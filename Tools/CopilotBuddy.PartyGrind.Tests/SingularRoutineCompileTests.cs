using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SingularRoutineCompileTests
    {
        [Fact]
        public void BotRoutines_CompilesCleanlyAtRuntime_AndExposesWarlockDestruction()
        {
            string? dir = AppContext.BaseDirectory;
            string? repoRoot = null;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "BotRoutines")) &&
                    File.Exists(Path.Combine(dir, "CopilotBuddy.csproj")))
                {
                    repoRoot = dir;
                    break;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }

            Assert.NotNull(repoRoot);
            string routinesPath = Path.Combine(repoRoot!, "BotRoutines");

            var collection = new ClassCollection<CombatRoutine>();
            collection.CompileAndLoadFrom(routinesPath, out CompilerResults results);

            Assert.NotNull(results);
            string errors = string.Join(Environment.NewLine,
                results.Errors.Cast<CompilerError>()
                    .Where(e => !e.IsWarning)
                    .Select(e => $"{e.FileName}:{e.Line}: {e.ErrorText}"));
            Assert.True(string.IsNullOrEmpty(errors), errors);

            Assert.NotEmpty(collection);

            var compiledAssembly = collection[0].GetType().Assembly;
            Type? commonHelpers = compiledAssembly.GetType("Singular.Helpers.Common");
            Assert.NotNull(commonHelpers);
            Assert.NotNull(commonHelpers!.GetMethod("CreatePetAttack"));

            Type? destrType = compiledAssembly.GetType("Singular.ClassSpecific.Warlock.Destruction");
            Assert.NotNull(destrType);
            Assert.NotNull(destrType!.GetMethod("CreateDestructionWarlockNormalPull"));
            Assert.NotNull(destrType.GetMethod("CreateDestructionWarlockNormalCombat"));
            Assert.NotNull(destrType.GetMethod("CreateDestructionWarlockPvPPullAndCombat"));
            Assert.NotNull(destrType.GetMethod("CreateDestructionWarlockInstancePullAndCombat"));

            Assert.NotNull(compiledAssembly.GetType("Singular.ClassSpecific.Warlock.Affliction"));
            Assert.NotNull(compiledAssembly.GetType("Singular.ClassSpecific.Warlock.Demonology"));
            Assert.NotNull(compiledAssembly.GetType("Singular.ClassSpecific.Warlock.Lowbie"));
        }

        [Fact]
        public void DungeonScripts_CompilesCleanlyAtRuntime_AndExposesUldaman()
        {
            string? dir = AppContext.BaseDirectory;
            string? repoRoot = null;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "Dungeon Scripts")) &&
                    File.Exists(Path.Combine(dir, "CopilotBuddy.csproj")))
                {
                    repoRoot = dir;
                    break;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }

            Assert.NotNull(repoRoot);
            string classicDir = Path.Combine(repoRoot!, "Dungeon Scripts", "Classic");
            Assert.True(File.Exists(Path.Combine(classicDir, "Uldaman.cs")));
            string deployedClassicDir = Path.Combine(AppContext.BaseDirectory, "Dungeon Scripts", "Classic");
            Assert.True(File.Exists(Path.Combine(deployedClassicDir, "Uldaman.cs")));

            _ = typeof(global::Bots.DungeonBuddy.Dungeon).Assembly;

            var loader = new Styx.Loaders.DynamicLoader<global::Bots.DungeonBuddy.Dungeon>(deployedClassicDir, compileSource: true);
            Assert.NotNull(loader.CompilerResults);
            string errors = string.Join(Environment.NewLine,
                loader.CompilerResults!.Errors.Cast<CompilerError>()
                    .Where(e => !e.IsWarning)
                    .Select(e => $"{e.FileName}:{e.Line}: {e.ErrorText}"));
            Assert.True(string.IsNullOrEmpty(errors), errors);

            var uldaman = loader.FirstOrDefault(d => d.DungeonId == 22U);
            Assert.NotNull(uldaman);

            Type uldamanType = uldaman!.GetType();
            Assert.NotNull(uldamanType.GetMethod("BaelogsChest"));
            Assert.NotNull(uldamanType.GetMethod("KeystoneObject"));
            Assert.NotNull(uldamanType.GetMethod("AltarofTheKeepers"));
            Assert.NotNull(uldamanType.GetMethod("AltarofTheArchaedas"));
            Assert.NotNull(uldamanType.GetMethod("AncientTreasure"));
        }
    }
}
