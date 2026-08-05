using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NoBreakZone.EditorTools
{
	/// <summary>
	/// Dumps every MonoScript in the project to Editor/GameData/script_guids.csv as
	/// (fullName, fileID, guid) — the exact pair Unity writes into a prefab's m_Script.
	///
	/// Why this exists: the generator used to hardcode assembly guids copied from reference
	/// mods and the SDK examples. Those turned out to name assemblies that do not exist in
	/// this SDK install, so every generated prefab and ScriptableObject shipped a dangling
	/// script reference — the build stayed green and the game silently loaded nothing.
	/// Reference values are a guess; this table is what Unity actually resolves.
	///
	/// Invoked as:
	///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
	///     -executeMethod NoBreakZone.EditorTools.DumpScriptGuids.Dump
	///
	/// Rerun after a game or SDK update, then rerun genassets.py.
	/// </summary>
	public static class DumpScriptGuids
	{
		private const string OutputPath = "Assets/NoBreakZone/Editor/GameData/script_guids.csv";

		public static void Dump()
		{
			var exitCode = 1;

			try
			{
				var rows = new List<string[]>();
				var seen = new HashSet<string>();

				foreach (var path in AssetDatabase.GetAllAssetPaths())
				{
					// MonoScripts live in .cs files (one each) and in .dll files (one per type).
					if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
					    !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
					{
						if (asset is not MonoScript script)
						{
							continue;
						}

						// This is the authoritative pair: the same guid and fileID a prefab
						// reference to this script would carry.
						if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out var guid, out long fileId))
						{
							continue;
						}

						var type = script.GetClass();
						var fullName = type != null ? type.FullName : script.name;
						if (string.IsNullOrEmpty(fullName))
						{
							continue;
						}

						// A type can be reached through more than one path; keep the first.
						if (!seen.Add($"{fullName}|{fileId}|{guid}"))
						{
							continue;
						}

						var assembly = type != null ? type.Assembly.GetName().Name : "";
						rows.Add(new[] { fullName, fileId.ToString(), guid, assembly, path });
					}
				}

				rows.Sort((a, b) =>
				{
					var byName = string.CompareOrdinal(a[0], b[0]);
					return byName != 0 ? byName : string.CompareOrdinal(a[1], b[1]);
				});

				var sb = new StringBuilder();
				sb.Append("fullName,fileID,guid,assembly,path\n");
				foreach (var row in rows)
				{
					for (var i = 0; i < row.Length; i++)
					{
						if (i > 0)
						{
							sb.Append(',');
						}

						sb.Append(Escape(row[i]));
					}

					sb.Append('\n');
				}

				var full = Path.GetFullPath(OutputPath);
				Directory.CreateDirectory(Path.GetDirectoryName(full));
				// Write LF and no BOM: the repository is shared with a mac checkout.
				File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));

				Debug.Log($"[NBZ] wrote {rows.Count} script guids -> {OutputPath}");
				exitCode = 0;
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}

			EditorApplication.Exit(exitCode);
		}

		private static string Escape(string value)
		{
			// Generic type names carry commas and brackets, so quote defensively.
			if (value.IndexOfAny(new[] { ',', '"', '\n' }) < 0)
			{
				return value;
			}

			return '"' + value.Replace("\"", "\"\"") + '"';
		}
	}
}
