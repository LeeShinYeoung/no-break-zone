using System;
using System.IO;
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
		private const string ModSettingsAssetPath = "Assets/NoBreakZone.asset";

		// EditorPrefs key the Mod SDK window stores the game install path under.
		private const string GameInstallPathKey = "PugMod/SDKWindow/GamePath";

		// Custom command-line arg the wrapper script passes to override the export path.
		private const string ExportPathArg = "-nbzExportPath";

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
				ModBuilder.BuildMod(settings, modsPath, success => built = success, installInSubDirectory: true);

				if (built)
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
