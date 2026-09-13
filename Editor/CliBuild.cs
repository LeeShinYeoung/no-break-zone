using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PugMod;
using UnityEditor;
using UnityEngine;

namespace NoBreakZone.EditorTools
{
	/// <summary>
	/// Batch-mode entry point for building the NoBreakZone mod without opening the editor GUI.
	/// Wraps <see cref="ModBuilder.BuildMod"/> (the same call the Mod SDK window makes) so the
	/// build can run from a script.
	///
	/// Invoked by build.ps1 as:
	///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
	///     -executeMethod NoBreakZone.EditorTools.CliBuild.BuildToGame
	///     -nbzExportPath &lt;game Mods folder&gt;
	///     -logFile &lt;log&gt;
	///
	/// Exit code 0 = build succeeded, 1 = failed. Requires the editor to be closed
	/// (a running editor holds the project lock and batch mode cannot open it).
	/// </summary>
	public static class CliBuild
	{
		// Under Editor/, which is inside this repository and outside the shipped bundle.
		//
		// It used to sit at Assets/NoBreakZone.asset, one level above the repo, where the mod guid,
		// name, dependencies, modPath and Linux flag lived on one machine with nothing to restore
		// them from. That placement is the SDK's own convention -- all nine example mods keep the
		// asset as a sibling of their mod folder -- but the convention assumes the Unity project is
		// what you version control, and here the MOD is the repository and the project is not.
		//
		// Editor/ rather than the repo root because this configures the build and is not part of
		// what ships: everything outside Editor/ is swept into the bundle. The mod.io tab writes
		// its own <ModName>_modio.asset next to this file, so that stays out of the bundle too.
		private const string ModSettingsAssetPath = "Assets/NoBreakZone/Editor/NoBreakZone.asset";

		// EditorPrefs key the Mod SDK window stores the game install path under.
		private const string GameInstallPathKey = "PugMod/SDKWindow/GamePath";

		// Custom command-line arg the wrapper script passes to override the export path.
		private const string ExportPathArg = "-nbzExportPath";

		// Unity reports an unresolvable m_Script as a warning and carries on, so ModBuilder happily
		// packs the broken asset and the build exits 0. That is exactly how a bundle whose every
		// prefab and item definition was dead shipped while the build claimed success. Treat these
		// as build failures: a green build has to mean the assets are intact.
		private static readonly string[] BrokenScriptMarkers =
		{
			"is missing or no valid script is attached",
			"The referenced script on this Behaviour",
		};

		private static readonly List<string> BrokenScripts = new List<string>();

		public static void BuildToGame()
		{
			int exitCode = 1;

			try
			{
				var settings = AssetDatabase.LoadAssetAtPath<ModBuilderSettings>(ModSettingsAssetPath);
				if (settings == null)
				{
					Debug.LogError($"[NBZ] ModBuilderSettings not found at {ModSettingsAssetPath}");
					EditorApplication.Exit(exitCode);
					return;
				}

				var modsPath = ResolveModsPath();
				if (string.IsNullOrEmpty(modsPath))
				{
					Debug.LogError(
						$"[NBZ] Could not resolve the game Mods folder. Pass {ExportPathArg} <path> " +
						"or set the game path in the Mod SDK window (\"Find game files\").");
					EditorApplication.Exit(exitCode);
					return;
				}

				Directory.CreateDirectory(modsPath);
				Debug.Log($"[NBZ] Building '{settings.metadata.name}' -> {modsPath}");

				// BuildMod invokes its callback synchronously, so 'built' is set before we read it.
				var built = false;
				BrokenScripts.Clear();
				Application.logMessageReceived += OnLogMessage;
				try
				{
					ModBuilder.BuildMod(settings, modsPath, success => built = success, installInSubDirectory: true);
				}
				finally
				{
					Application.logMessageReceived -= OnLogMessage;
				}

				var broken = BrokenScripts.Distinct().ToList();
				if (broken.Count > 0)
				{
					// Reported after unsubscribing, so these lines cannot feed back into the list.
					Debug.LogError(
						$"[NBZ] BUILD FAILED - {broken.Count} asset(s) reference a script Unity could not " +
						"resolve. The bundle would load nothing. Run Editor/preflight.py to see which " +
						"references are dead.");
					foreach (var line in broken)
					{
						Debug.LogError($"[NBZ]   {line}");
					}
				}
				else if (built)
				{
					var installedAt = Path.Combine(modsPath, settings.metadata.name);
					Debug.Log($"[NBZ] BUILD OK -> {installedAt}");
					exitCode = 0;
				}
				else
				{
					Debug.LogError("[NBZ] BUILD FAILED (see log above)");
				}
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}

			EditorApplication.Exit(exitCode);
		}

		private static void OnLogMessage(string condition, string stackTrace, LogType type)
		{
			foreach (var marker in BrokenScriptMarkers)
			{
				if (condition.IndexOf(marker, StringComparison.Ordinal) >= 0)
				{
					BrokenScripts.Add(condition);
					return;
				}
			}
		}

		private static string ResolveModsPath()
		{
			// 1) Explicit override from the wrapper script.
			var args = Environment.GetCommandLineArgs();
			for (var i = 0; i < args.Length - 1; i++)
			{
				if (string.Equals(args[i], ExportPathArg, StringComparison.Ordinal))
				{
					return args[i + 1];
				}
			}

			// 2) Fall back to the game install path stored by the Mod SDK window.
			if (!EditorPrefs.HasKey(GameInstallPathKey))
			{
				return null;
			}

			var gamePath = EditorPrefs.GetString(GameInstallPathKey);
			if (Directory.Exists(Path.Combine(gamePath, "CoreKeeper_Data")))
			{
				return Path.Combine(gamePath, "CoreKeeper_Data", "StreamingAssets", "Mods");
			}
			if (Directory.Exists(Path.Combine(gamePath, "CoreKeeperServer_Data")))
			{
				return Path.Combine(gamePath, "CoreKeeperServer_Data", "StreamingAssets", "Mods");
			}

			return null;
		}
	}
}
