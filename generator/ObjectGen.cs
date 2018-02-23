﻿// GtkSharp.Generation.ObjectGen.cs - The Object Generatable.
//
// Author: Mike Kestner <mkestner@ximian.com>
//
// Copyright (c) 2001-2003 Mike Kestner
// Copyright (c) 2003-2004 Novell, Inc.
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of version 2 of the GNU General Public
// License as published by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public
// License along with this program; if not, write to the
// Free Software Foundation, Inc., 59 Temple Place - Suite 330,
// Boston, MA 02111-1307, USA.
using System.Collections.Generic;

using System;
using System.IO;
using System.Xml;
using System.Linq;

namespace GtkSharp.Generation {

	public class ObjectGen : ObjectBase  {

		List<string> custom_attrs = new List<string>();
		List<XmlNode> strings = new List<XmlNode>();
		List<XmlElement> vm_nodes = new List<XmlElement>();
		Dictionary<string, ChildProperty> childprops = new Dictionary<string, ChildProperty>();
		static Dictionary<string, DirectoryInfo> dirs = new Dictionary<string, DirectoryInfo>();

		public ObjectGen (XmlElement ns, XmlElement elem) : base (ns, elem) 
		{
			foreach (XmlNode node in elem.ChildNodes) {
				string name;

				if (!(node is XmlElement)) continue;
				XmlElement member = (XmlElement) node;

				switch (node.Name) {
				case Constants.Callback:
					Statistics.IgnoreCount++;
					break;

				case Constants.CustomAttribute:
					custom_attrs.Add (member.InnerXml);
					break;

				case Constants.VirtualMethod:
					Statistics.IgnoreCount++;
					break;

				case Constants.StaticString:
					strings.Add (node);
					break;

				case Constants.ChildProp:
					name = member.GetAttribute (Constants.Name);
					while (childprops.ContainsKey (name))
						name += "mangled";
					childprops.Add (name, new ChildProperty (member, this));
					break;

				default:
					if (!IsNodeNameHandled (node.Name))
						Console.WriteLine ("Unexpected node " + node.Name + " in " + CName);
					break;
				}
			}
		}

		public override bool Validate ()
		{
			var invalids = new List<string>();

			foreach (var prop in childprops) {
				if (!prop.Value.Validate ()) {
					Console.WriteLine ("in Object " + QualifiedName);
					invalids.Add (prop.Key);
				}
			}
			foreach (var item in invalids)
				childprops.Remove (item);

			return base.Validate ();
		}

		protected override void ParseElement(XmlElement ns, XmlElement elem)
		{
			base.ParseElement (ns, elem);

			DisableVoidCtor = elem.HasAttribute (Constants.DisableVoidCtor);
			DisableGTypeCtor = elem.HasAttribute (Constants.DisableGTypeCtor);
			parentType = elem.GetAttribute (Constants.Parent);
		}

		string parentType;
		private bool DisableVoidCtor;

		private bool DisableGTypeCtor;

		private class DirectoryInfo {
			public string assembly_name;
			public Dictionary<string, string> objects;

			public DirectoryInfo (string assembly_name) {
				this.assembly_name = assembly_name;
				objects = new Dictionary<string, string>();
			}
		}

		private static DirectoryInfo GetDirectoryInfo (string dir, string assembly_name)
		{
			DirectoryInfo result;

			if (dirs.ContainsKey (dir)) {
				result = dirs [dir] as DirectoryInfo;
				if  (result.assembly_name != assembly_name) {
					Console.WriteLine ("Can't put multiple assemblies in one directory.");
					return null;
				}

				return result;
			}

			result = new DirectoryInfo (assembly_name);
			dirs.Add (dir, result);
			
			return result;
		}

		ClassBase GetParentWithGType (ClassBase start)
		{
			ClassBase parent = start;
			while (parent != null && parent.CName != "GObject") {
				if (parent.GetMethod ("GetType") == null && parent.GetMethod ("GetGType") == null) {
					parent = Parent;
				} else {
					return parent;
				}
			}
			return null;
		}

		protected void GenerateAttribute (StreamWriter writer)
		{
			var parent = GetParentWithGType (this);
			if (parent != null)
				writer.WriteLine ("\t[{0}]", parent.Name);
			else
				writer.WriteLine ("\t[GLib.GTypeObject]");
		}

		public override void Generate (GenerationInfo gen_info)
		{
			gen_info.CurrentType = Name;

			string asm_name = gen_info.AssemblyName.Length == 0 ? NS.ToLower () + "-sharp" : gen_info.AssemblyName;
			DirectoryInfo di = GetDirectoryInfo (gen_info.Dir, asm_name);

			StreamWriter sw = gen_info.Writer = gen_info.OpenStream (Name);

			sw.WriteLine ("namespace " + NS + " {");
			sw.WriteLine ();
			sw.WriteLine ("\tusing System;");
			sw.WriteLine ("\tusing System.Collections;");
			sw.WriteLine ("\tusing System.Runtime.InteropServices;");
			sw.WriteLine ();

			SymbolTable table = SymbolTable.Table;

			sw.WriteLine ("#region Autogenerated code");
			if (IsDeprecated)
				sw.WriteLine ("\t[Obsolete]");
			foreach (string attr in custom_attrs)
				sw.WriteLine ("\t" + attr);
			GenerateAttribute (sw);
			sw.Write ("\t{0} {1}class " + Name, IsInternal ? "internal" : "public", IsAbstract ? "abstract " : "");
			string cs_parent = table.GetCSType(parentType);
			if (cs_parent != "") {
				di.objects.Add (CName, QualifiedName);
				sw.Write (" : " + cs_parent);
			}
			foreach (string iface in interfaces) {
				if (Parent != null && Parent.Implements (iface))
					continue;
				sw.Write (", " + table.GetCSType (iface));
			}
			foreach (string iface in managed_interfaces) {
				if (Parent != null && Parent.Implements (iface))
					continue;
				sw.Write (", " + iface);
			}
			sw.WriteLine (" {");
			sw.WriteLine ();

			GenCtors (gen_info);
			GenProperties (gen_info, null);
			GenFields (gen_info);
			GenChildProperties (gen_info);
			
			bool has_sigs = (sigs != null && sigs.Count > 0);
			if (!has_sigs) {
				foreach (string iface in interfaces) {
					ClassBase igen = table.GetClassGen (iface);
					if (igen != null && igen.Signals != null) {
						has_sigs = true;
						break;
					}
				}
			}

			if (has_sigs && !string.IsNullOrEmpty (parentType)) {
				GenSignals (gen_info, null);
			}

			if (vm_nodes.Count > 0) {
				if (gen_info.GlueEnabled) {
					GenVirtualMethods (gen_info);
				} else {
					Statistics.VMIgnored = true;
					Statistics.ThrottledCount += vm_nodes.Count;
				}
			}

			GenMethods (gen_info, null, null);
			
			if (interfaces.Count != 0) {
				Dictionary<string, Method> all_methods = new Dictionary<string, Method>();
				foreach (Method m in Methods.Values)
					all_methods[m.Name] = m;
				var collisions = new Dictionary<string, bool>();
				foreach (string iface in interfaces) {
					ClassBase igen = table.GetClassGen (iface);
					foreach (Method m in igen.Methods.Values) {
						Method collision;
						if (all_methods.TryGetValue (m.Name, out collision) && collision.Signature.Types == m.Signature.Types)
							collisions[m.Name] = true;
						else
							all_methods[m.Name] = m;
					}
				}
					
				foreach (string iface in interfaces) {
					if (Parent != null && Parent.Implements (iface))
						continue;
					ClassBase igen = table.GetClassGen (iface);
					igen.GenMethods (gen_info, collisions, this);
					igen.GenProperties (gen_info, this);
					igen.GenSignals (gen_info, this);
				}
			}

			foreach (XmlElement str in strings) {
				sw.Write ("\t\tpublic static string " + str.GetAttribute (Constants.Name));
				sw.WriteLine (" {\n\t\t\t get { return \"" + str.GetAttribute (Constants.Value) + "\"; }\n\t\t}");
			}

			if (cs_parent != String.Empty && GetExpected (CName) != QualifiedName) {
				sw.WriteLine ();
				sw.WriteLine ("\t\tstatic " + Name + " ()");
				sw.WriteLine ("\t\t{");
				sw.WriteLine ("\t\t\tGtkSharp." + Studlify (asm_name) + ".ObjectManager.Initialize ();");
				sw.WriteLine ("\t\t}");
			}

			sw.WriteLine ("#endregion");
			AppendCustom (sw, gen_info.CustomDir);

			sw.WriteLine ("\t}");
			var parentGType = GetParentWithGType (this);
			if (parentGType == this) {
				var method = parentGType.GetMethod ("GetType") ?? parentGType.GetMethod ("GetGType");
				AttributeHelper.Gen (sw, Name, LibraryName, method.CName);
			}
			sw.WriteLine ("}");

			sw.Close ();
			gen_info.Writer = null;
			Statistics.ObjectCount++;
		}

		protected override void GenCtors (GenerationInfo gen_info)
		{
			if (!string.IsNullOrEmpty (parentType))
				return;

			if (!DisableGTypeCtor) {
				gen_info.Writer.WriteLine("\t\t[Obsolete]");
				gen_info.Writer.WriteLine("\t\tprotected " + Name + "(GLib.GType gtype) : base(gtype) {}");
			}
			gen_info.Writer.WriteLine("\t\tpublic " + Name + "(IntPtr raw) : base(raw) {}");
			if (ctors.Count == 0 && !DisableVoidCtor) {
				gen_info.Writer.WriteLine();
				gen_info.Writer.WriteLine("\t\tprotected " + Name + "() : base(IntPtr.Zero)");
				gen_info.Writer.WriteLine("\t\t{");
				gen_info.Writer.WriteLine("\t\t\tCreateNativeObject (Array.Empty<IntPtr> (), Array.Empty<GLib.Value> (), 0);");
				gen_info.Writer.WriteLine("\t\t}");
			}
			gen_info.Writer.WriteLine();

			base.GenCtors (gen_info);
		}

		protected void GenChildProperties (GenerationInfo gen_info)
		{
			if (childprops.Count == 0)
				return;

			StreamWriter sw = gen_info.Writer;

			ObjectGen child_ancestor = Parent as ObjectGen;
			while (child_ancestor.CName != "GtkContainer" &&
			       child_ancestor.childprops.Count == 0)
				child_ancestor = child_ancestor.Parent as ObjectGen;

			sw.WriteLine ("\t\tpublic class " + Name + "Child : " + child_ancestor.NS + "." + child_ancestor.Name + "." + child_ancestor.Name + "Child {");
			sw.WriteLine ("\t\t\tprotected internal " + Name + "Child (Gtk.Container parent, Gtk.Widget child) : base (parent, child) {}");
			sw.WriteLine ("");

			foreach (ChildProperty prop in childprops.Values)
				prop.Generate (gen_info, "\t\t\t", null);

			sw.WriteLine ("\t\t}");
			sw.WriteLine ("");

			sw.WriteLine ("\t\tpublic override Gtk.Container.ContainerChild this [Gtk.Widget child] {");
			sw.WriteLine ("\t\t\tget {");
			sw.WriteLine ("\t\t\t\treturn new " + Name + "Child (this, child);");
			sw.WriteLine ("\t\t\t}");
			sw.WriteLine ("\t\t}");
			sw.WriteLine ("");
			
		}

		private void GenVMGlue (GenerationInfo gen_info, XmlElement elem)
		{
			StreamWriter sw = gen_info.GlueWriter;

			string vm_name = elem.GetAttribute (Constants.CName);
			string method = gen_info.GluelibName + "_" + NS + Name + "_override_" + vm_name;
			sw.WriteLine ();
			sw.WriteLine ("void " + method + " (GType type, gpointer cb);");
			sw.WriteLine ();
			sw.WriteLine ("void");
			sw.WriteLine (method + " (GType type, gpointer cb)");
			sw.WriteLine ("{");
			sw.WriteLine ("\t{0} *klass = ({0} *) g_type_class_peek (type);", NS + Name + "Class");
			sw.WriteLine ("\tklass->" + vm_name + " = cb;");
			sw.WriteLine ("}");
		}

		static bool vmhdrs_needed = true;

		private void GenVirtualMethods (GenerationInfo gen_info)
		{
			if (vmhdrs_needed) {
				gen_info.GlueWriter.WriteLine ("#include <glib-object.h>");
				gen_info.GlueWriter.WriteLine ("#include \"vmglueheaders.h\"");
				gen_info.GlueWriter.WriteLine ();
				vmhdrs_needed = false;
			}

			foreach (XmlElement elem in vm_nodes) {
				GenVMGlue (gen_info, elem);
			}
		}

		/* Keep this in sync with the one in glib/GType.cs */
		private static string GetExpected (string cname)
		{
			for (int i = 1; i < cname.Length; i++) {
				if (Char.IsUpper (cname[i])) {
					if (i == 1 && cname[0] == 'G')
						return "GLib." + cname.Substring (1);
					else
						return cname.Substring (0, i) + "." + cname.Substring (i);
				}
			}

			throw new ArgumentException ("cname doesn't follow the NamespaceType capitalization style: " + cname);
		}

		private static bool NeedsMap (Dictionary<string, string> objs, string assembly_name)
		{
			foreach (string key in objs.Keys)
				if (GetExpected (key) != objs[key])
					return true;
			
			return false;
		}

		private static string Studlify (string name)
		{
			string result = "";

			string[] subs = name.Split ('-');
			foreach (string sub in subs)
				result += Char.ToUpper (sub[0]) + sub.Substring (1);
				
			return result;
		}
				
		public static void GenerateMappers ()
		{
			foreach (string dir in dirs.Keys) {

				DirectoryInfo di = dirs[dir] as DirectoryInfo;

				if (!NeedsMap (di.objects, di.assembly_name))
					continue;
	
				GenerationInfo gen_info = new GenerationInfo (dir, di.assembly_name);

				GenerateMapper (di, gen_info);
			}
		}

		private static void GenerateMapper (DirectoryInfo dir_info, GenerationInfo gen_info)
		{
			StreamWriter sw = gen_info.OpenStream ("ObjectManager");

			sw.WriteLine ("namespace GtkSharp." + Studlify (dir_info.assembly_name) + " {");
			sw.WriteLine ();
			sw.WriteLine ("\tpublic class ObjectManager {");
			sw.WriteLine ();
			sw.WriteLine ("\t\tstatic bool initialized = false;");
			sw.WriteLine ("\t\t// Call this method from the appropriate module init function.");
			sw.WriteLine ("\t\tpublic static void Initialize ()");
			sw.WriteLine ("\t\t{");
			sw.WriteLine ("\t\t\tif (initialized)");
			sw.WriteLine ("\t\t\t\treturn;");
			sw.WriteLine ("");
			sw.WriteLine ("\t\t\tinitialized = true;");
	
			foreach (string key in dir_info.objects.Keys) {
				if (GetExpected(key) != ((string) dir_info.objects[key]))
					sw.WriteLine ("\t\t\tGLib.GType.Register ({0}.GType, typeof ({0}));", dir_info.objects [key]);
			}
					
			sw.WriteLine ("\t\t}");
			sw.WriteLine ("\t}");
			sw.WriteLine ("}");
			sw.Close ();
		}
	}
}

