using System;
using UnityEditor;
using UnityEngine;

namespace NoBreakZone.EditorTools
{
    /// <summary>
    /// Batch-mode entry point for the ECS checks — the second rung of the verification ladder
    /// (Editor/Docs/workflow.md). Run through Editor/verify.ps1:
    ///
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
    ///     -executeMethod NoBreakZone.EditorTools.CliVerify.All
    ///     -logFile &lt;log&gt;
    ///
    /// Exit code 0 = every check passed, 1 = something failed or threw.
    ///
    /// WHY NOT THE UNITY TEST RUNNER. `Unity.exe -batchmode -runTests` is refused on this machine:
    /// it loads the project and then exits 198 with "No valid Unity Editor license found", without
    /// running a single test. `-batchmode -quit -executeMethod` — the path build.ps1 already uses —
    /// is unaffected, so this borrows that path instead of fighting the licence.
    ///
    /// WHAT THIS TIER CANNOT PROVE. Every check here runs against a hand-built World with hand-built
    /// entities. It can prove that the mod's systems sort where they claim to and do what they claim
    /// to when handed a given world state. It cannot prove that the real game hands them that state:
    /// the game's own systems are Burst jobs that need a database, a tilemap and a NetCode world, so
    /// they are not created here. That is what the in-game self test is for.
    /// </summary>
    public static class CliVerify
    {
        public static void All()
        {
            int exitCode = 1;

            try
            {
                var report = new VerifyReport();

                VerifySystemOrder.Run(report);
                VerifyProtectionBehaviour.Run(report);

                Debug.Log($"[NBZ] verify: {report.Passed} passed, {report.Failed} failed");
                exitCode = report.Failed == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                // Never rethrow out of a batch entry point: the editor reports it oddly and the
                // exit code stops meaning anything.
                Debug.LogException(e);
            }

            EditorApplication.Exit(exitCode);
        }
    }

    /// Collects results so one failure does not hide the rest. Every line is prefixed [NBZ] so the
    /// wrapper script can tail the log the same way build.ps1 does.
    public sealed class VerifyReport
    {
        public int Passed;
        public int Failed;

        public void Check(bool condition, string what)
        {
            if (condition)
            {
                Passed++;
                Debug.Log("[NBZ]   ok: " + what);
                return;
            }

            Failed++;
            Debug.LogError("[NBZ] FAIL: " + what);
        }
    }
}
