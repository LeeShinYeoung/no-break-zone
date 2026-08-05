using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NoBreakZone.EditorTools
{
	/// <summary>
	/// Dumps the shape of game types — fields, properties, enum values — to
	/// Editor/GameData/type_members.txt.
	///
	/// Why this exists: mod code cannot use reflection (the game's safety check rejects the whole
	/// assembly for it), so every time a diagnostic needed a game type's field name it was a guess,
	/// and a wrong guess costs a full build and a game restart to discover. Editor code has no such
	/// restriction. Running this once turns "what is this field called" from a round trip into a
	/// lookup.
	///
	/// Invoked as:
	///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt;
	///     -executeMethod NoBreakZone.EditorTools.DumpTypeMembers.Dump
	///
	/// Add names to Wanted and rerun when a new type comes up.
	/// </summary>
	public static class DumpTypeMembers
	{
		private const string OutputPath = "Assets/NoBreakZone/Editor/GameData/type_members.txt";

		// Simple names, matched against every loaded assembly. Namespaces vary and are not worth
		// tracking here.
		private static readonly string[] Wanted =
		{
			// what a recipe is, and what the crafting UI groups by
			"ObjectCategoryTag",
			"ObjectCategoryTagsCD",
			"CanCraftObjectsBuffer",
			"CraftingAuthoring",
			"CraftableObject",
			"ObjectInfo",
			"ObjectAuthoring",
			"InventoryItemAuthoring",
			// the container that ran out of room
			"SimpleCraftingUIContainer",
			"SimpleCraftingUI",
			"CraftingCategoryNavigationUI",
			"CraftingUIContainer",
			"CraftingBuilding",
			"ObjectType",
			"Rarity",
		};

		public static void Dump()
		{
			var exitCode = 1;

			try
			{
				var sb = new StringBuilder();
				var matched = 0;

				foreach (var name in Wanted)
				{
					foreach (var type in FindTypes(name))
					{
						matched++;
						Describe(sb, type);
					}
				}

				var full = Path.GetFullPath(OutputPath);
				Directory.CreateDirectory(Path.GetDirectoryName(full));
				File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));

				Debug.Log($"[NBZ] dumped {matched} type(s) -> {OutputPath}");
				exitCode = 0;
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}

			EditorApplication.Exit(exitCode);
		}

		private static IEnumerable<Type> FindTypes(string simpleName)
		{
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types.Where(t => t != null).ToArray();
				}
				catch
				{
					continue;
				}

				foreach (var type in types)
				{
					if (type.Name == simpleName || type.Name.StartsWith(simpleName + "`", StringComparison.Ordinal))
					{
						yield return type;
					}
				}
			}
		}

		private static void Describe(StringBuilder sb, Type type)
		{
			sb.Append("=== ").Append(type.FullName)
				.Append("   [").Append(type.Assembly.GetName().Name).Append("]\n");

			if (type.IsEnum)
			{
				// Enum values are the whole point for ObjectCategoryTag: the number a prefab carries
				// means nothing without them.
				var names = Enum.GetNames(type);
				var values = Enum.GetValues(type);
				for (var i = 0; i < names.Length; i++)
				{
					sb.Append("    ").Append(Convert.ToInt64(values.GetValue(i)))
						.Append(" = ").Append(names[i]).Append('\n');
				}

				sb.Append('\n');
				return;
			}

			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
				| BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

			foreach (var field in type.GetFields(flags))
			{
				// Visibility decides whether mod code can touch it at all: reflection is banned in
				// a mod, so a private field is out of reach whatever it holds.
				var access = field.IsPublic ? "public " : field.IsFamily ? "protected " : "private ";

				sb.Append("    field  ").Append(access).Append(Pretty(field.FieldType))
					.Append(' ').Append(field.Name);

				// A constant decides whether a limit can be worked around or has to be designed
				// around, so print the number rather than just its name.
				if (field.IsLiteral || (field.IsStatic && field.IsInitOnly))
				{
					sb.Append(" = ").Append(StaticValue(field));
				}
				else if (field.IsStatic)
				{
					sb.Append("   (static)");
				}

				sb.Append('\n');
			}

			foreach (var property in type.GetProperties(flags))
			{
				sb.Append("    prop   ").Append(Pretty(property.PropertyType))
					.Append(' ').Append(property.Name).Append('\n');
			}

			foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
			{
				sb.Append("    nested ").Append(nested.Name).Append('\n');
			}

			sb.Append('\n');

			foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
			{
				Describe(sb, nested);
			}
		}

		private static string StaticValue(FieldInfo field)
		{
			try
			{
				return Convert.ToString(field.GetValue(null)) ?? "null";
			}
			catch (Exception e)
			{
				return "<unreadable: " + e.GetType().Name + ">";
			}
		}

		private static string Pretty(Type type)
		{
			if (!type.IsGenericType)
			{
				return type.Name;
			}

			var args = string.Join(",", type.GetGenericArguments().Select(Pretty));
			return type.Name.Split('`')[0] + "<" + args + ">";
		}
	}
}
