#region Copyright (c) 2007 Ryan Williams <drcforbin@gmail.com>
/// <copyright>
/// Copyright (c) 2007 Ryan Williams <drcforbin@gmail.com>
/// 
/// Permission is hereby granted, free of charge, to any person obtaining a copy
/// of this software and associated documentation files (the "Software"), to deal
/// in the Software without restriction, including without limitation the rights
/// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
/// copies of the Software, and to permit persons to whom the Software is
/// furnished to do so, subject to the following conditions:
/// 
/// The above copyright notice and this permission notice shall be included in
/// all copies or substantial portions of the Software.
/// 
/// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
/// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
/// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
/// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
/// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
/// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
/// THE SOFTWARE.
/// </copyright>
#endregion
using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using Obfuscar.Helpers;

namespace Obfuscar
{
	class Project : IEnumerable<AssemblyInfo>
	{
		private const string SPECIALVAR_PROJECTFILEDIRECTORY = "ProjectFileDirectory";
		private readonly List<AssemblyInfo> assemblyList = new List<AssemblyInfo> ();

		public IEnumerable<AssemblyInfo> CopiedAssemblies {
			get {
				return copiedAssemblies.Values;
			}
		}

		private readonly Dictionary<string, AssemblyInfo> copiedAssemblies = new Dictionary<string, AssemblyInfo> ();
		private readonly Dictionary<string, AssemblyInfo> assemblyMap = new Dictionary<string, AssemblyInfo> ();
		private readonly Variables vars = new Variables ();
		InheritMap inheritMap;
		Settings settings;
		// FIXME: Figure out why this exists if it is never used.
		//private RSA keyvalue;
		// don't create.  call FromXml.
		private Project ()
		{
		}

		public string [] ExtraPaths {
			get {
				return vars.GetValue ("ExtraFrameworkFolders", "").Split (new char [] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
			}
		}

		public string KeyContainerName;
		public byte[] keyPair;

		public byte[] KeyPair {
			get {
				if (keyPair != null)
					return keyPair;

				var lKeyFileName = vars.GetValue ("KeyFile", null);
				var lKeyContainerName = vars.GetValue ("KeyContainer", null);

				if (lKeyFileName == null && lKeyContainerName == null)
					return null;
				if (lKeyFileName != null && lKeyContainerName != null)
					throw new ObfuscarException ("'Key file' and 'Key container' properties cann't be setted together.");

				try {
					keyPair = File.ReadAllBytes (vars.GetValue ("KeyFile", null));
				} catch (Exception ex) {
					throw new ObfuscarException (String.Format ("Failure loading key file \"{0}\"", vars.GetValue ("KeyFile", null)), ex);
				}

				return keyPair;
			}
		}

		public RSA KeyValue {
			get {
				//if (keyvalue != null)
				//	return keyvalue;

				var lKeyFileName = vars.GetValue ("KeyFile", null);
				var lKeyContainerName = vars.GetValue ("KeyContainer", null);

				if (lKeyFileName == null && lKeyContainerName == null)
					return null;
				if (lKeyFileName != null && lKeyContainerName != null)
					throw new ObfuscarException ("'Key file' and 'Key container' properties cann't be setted together.");

				if (vars.GetValue ("KeyContainer", null) != null) {
					KeyContainerName = vars.GetValue ("KeyContainer", null);
					if (Type.GetType ("System.MonoType") != null)
						throw new ObfuscarException ("Key containers are not supported for Mono.");
				} 
   				
				return null;
				//return keyvalue;
			}
		}

		AssemblyCache m_cache;

		internal AssemblyCache Cache {
			get {
				if (m_cache == null) {
					m_cache = new AssemblyCache (this);
				}

				return m_cache;
			}
			set { m_cache = value; }
		}

		public static Project FromXml (XmlReader reader, string projectFileDirectory)
		{
			Project project = new Project ();

			project.vars.Add (SPECIALVAR_PROJECTFILEDIRECTORY, string.IsNullOrEmpty (projectFileDirectory) ? "." : projectFileDirectory);

			while (reader.Read ()) {
				if (reader.NodeType == XmlNodeType.Element) {
					switch (reader.Name) {
					case "Var":
						{
							string name = Helper.GetAttribute (reader, "name");
							if (name.Length > 0) {
								string value = Helper.GetAttribute (reader, "value");
								if (value.Length > 0)
									project.vars.Add (name, value);
								else
									project.vars.Remove (name);
							}
							break;
						}
					case "Module":
						AssemblyInfo info = AssemblyInfo.FromXml (project, reader, project.vars);
						if (info.Exclude) {
							project.copiedAssemblies.Add (info.Name, info);
							break;
						}
						Console.WriteLine ("Processing assembly: " + info.Definition.Name.FullName);
						project.assemblyList.Add (info);
						project.assemblyMap [info.Name] = info;
						break;
					}
				}
			}

            if (project.Settings.UpdateOtherModuleReferences) {
                //project.AddReferencingAssemblies ();
            }

			return project;
		}

        private sealed class InputAssemblyVertex
        {
            private readonly HashSet<string> references = new HashSet<string> ();
            private readonly string path;
            private readonly string name;
            private readonly bool mixed;
            public InputAssemblyVertex (string path)
            {
                this.path = path;
                var assemblyDef = AssemblyDefinition.ReadAssembly (path);
                name = assemblyDef.Name.Name;
                foreach (var reference in assemblyDef.MainModule.AssemblyReferences) {
                    references.Add (reference.Name);
                }
                mixed = (assemblyDef.MainModule.Attributes & ModuleAttributes.ILOnly) == 0;
            }
            public string FilePath { get { return path; } }
            public string Name { get { return name; } }
            public bool IsMixed { get { return mixed; } }
            public IEnumerable<string> References {  get { return references; } }
        }

        private sealed class InputAssemblyVertexCollection : KeyedCollection<string, InputAssemblyVertex>
        {
            protected override string GetKeyForItem (InputAssemblyVertex item)
            {
                return item.Name;
            }
        }

        private sealed class InputAssemblyGraph
        {
            private sealed class Index
            {
                private readonly Dictionary<string, int> indexOf = new Dictionary<string, int> ();
                private readonly string[] nameOf;
                public Index (ICollection<string> names)
                {
                    nameOf = new string [names.Count];
                    foreach (var name in names) {
                        var index = indexOf.Count;
                        nameOf [index] = name;
                        indexOf.Add (name, index);
                    }
                }
                public int this [string name] { get { return indexOf [name]; } }
                public string this [int index] { get { return nameOf [index]; } }
                public int Count { get { return nameOf.Length; } }
            }

            private sealed class AdjacencyList
            {
                private readonly HashSet<int>[] content;
                private readonly HashSet<int> roots;
                private readonly Index index;
                private AdjacencyList (HashSet<int>[] content, HashSet<int> roots, Index index)
                {
                    this.content = content;
                    this.roots = roots;
                    this.index = index;
                }
                private static HashSet<int>[] CreateEmpty (int count)
                {
                    var array = new HashSet<int> [count];
                    for (int i = 0; i < count; i++) {
                        array [i] = new HashSet<int> ();
                    }
                    return array;
                }
                public static AdjacencyList Create (ICollection<InputAssemblyVertex> assemblyVertices, Index index)
                {
                    var roots = new HashSet<int> (Enumerable.Range (0, assemblyVertices.Count));
                    var content = CreateEmpty (index.Count);
                    foreach (var assemblyVertex in assemblyVertices) {
                        var children = assemblyVertex.References.Select (r => index [r]).ToList ();
                        content [index [assemblyVertex.Name]].AddRange (children);
                        roots.RemoveRange (children);
                    }
                    return new AdjacencyList (content, roots, index);
                }
                public AdjacencyList Reverse ()
                {
                    var newRoots = new HashSet<int> ();
                    var reversed = CreateEmpty (index.Count);
                    for (int i = 0; i < content.Length; i++) {
                        if (content [i].Count == 0) {
                            newRoots.Add (i);
                        } else {
                            foreach (var j in content [i]) {
                                reversed [j].Add (i);
                            }
                        }
                    }
                    return new AdjacencyList (reversed, newRoots, index);
                }
                public void Bfs (Action<string, string> visit)
                {
                    var visited = new bool [content.Length];
                    var queue = new Queue<int> (roots);
                    while (queue.Count > 0) {
                        var vertex = queue.Dequeue ();
                        if (!visited [vertex]) {
                            visited [vertex] = true;
                            foreach (var child in content [vertex]) {
                                if (visit != null) {
                                    visit (index [vertex], index [child]);
                                }
                                if (!visited [child]) {
                                    queue.Enqueue (child);
                                }
                            }
                        }
                    }
                }
            }

            private readonly InputAssemblyVertexCollection assemblyVertices;
            private readonly HashSet<string> obfuscated;
            private HashSet<string> GetAllNames ()
            {
                var all = new HashSet<string> ();
                foreach (var vertex in assemblyVertices) {
                    all.Add (vertex.Name);
                    all.AddRange (vertex.References);
                }
                return all;
            }
            private void Visit (HashSet<string> referencing, string fromName, string toName)
            {
                if (obfuscated.Contains (fromName) && !obfuscated.Contains (toName)) {
                    referencing.Add (toName);
                }
            }
            private IEnumerable<string> FindReferencingAssemblyNames ()
            {
                var referencing = new HashSet<string> ();
                var allNames = GetAllNames ();
                var index = new Index (allNames);
                var adjacencyList = AdjacencyList.Create (assemblyVertices, index);
                var reverseAdjacencyList = adjacencyList.Reverse ();
                reverseAdjacencyList.Bfs ( (from, to) => Visit (referencing, from, to));
                return referencing;
            }
            public InputAssemblyGraph (Project project)
            {
                assemblyVertices = project.LoadVertices ();
                obfuscated = new HashSet<string> (project.assemblyMap.Keys);
            }
            public IEnumerable<string> FindReferencingAssemblyPaths ()
            {
                return FindReferencingAssemblyNames ().Select (n => assemblyVertices [n].FilePath);
            }
            public bool IsMixed (string name)
            {
                return assemblyVertices [name].IsMixed;
            }
        }

        private InputAssemblyVertexCollection LoadVertices ()
        {
            var vertices = new InputAssemblyVertexCollection ();
            foreach (var binaryPath in GetInPathBinaries ()) {
                try {
                    vertices.Add (new InputAssemblyVertex (binaryPath));
                } catch (BadImageFormatException) {
                }
            }
            return vertices;
        }

        private void AddReferencingAssemblies ()
        {
            var graph = new InputAssemblyGraph (this);
            foreach (var filePath in graph.FindReferencingAssemblyPaths ()) { 
                var info = AssemblyInfo.FromReference (filePath, this);
                if (graph.IsMixed (info.Name)) { 
                    Console.WriteLine ("Will not update references in '{0}' (mixed mode assemblies are not supported)", info.Name);
                } else {
                    Console.WriteLine("Processing assembly: " + info.Definition.Name.FullName);
                    assemblyList.Add (info);
                    assemblyMap[info.Name] = info;
                }
            }
        }

        private IEnumerable<string> GetInPathBinaries ()
        { 
            foreach (var path in Directory.GetFiles (Settings.InPath)) { 
                if (IsBinaryFile (path)) {
                    yield return path;
                }
            }
        }

        private static bool IsBinaryFile (string path)
        {
            var extension = Path.GetExtension (path);
            if (extension != null) {
                extension = extension.ToLowerInvariant ();
                return extension == ".dll" || extension == ".exe";
            }
            return false;
        }

        private class Graph
		{
			private readonly List<Node<AssemblyInfo>> Root = new List<Node<AssemblyInfo>> ();

			public Graph (List<AssemblyInfo> items)
			{
				foreach (var item in items)
					Root.Add (new Node<AssemblyInfo> { Item = item });

				AddParents (Root);
			}

			private static void AddParents (List<Node<AssemblyInfo>> nodes)
			{
				foreach (var node in nodes) {
					var references = node.Item.References;
					foreach (var reference in references) {
						var parent = SearchNode (reference, nodes);
						node.AppendTo (parent);
					}
				}
			}

			private static Node<AssemblyInfo> SearchNode (AssemblyInfo baseType, List<Node<AssemblyInfo>> nodes)
			{
				return nodes.FirstOrDefault (node => node.Item == baseType);
			}

			internal IEnumerable<AssemblyInfo> GetOrderedList ()
			{
				var result = new List<AssemblyInfo> ();
				CleanPool (Root, result);
				return result;
			}

			private static void CleanPool (List<Node<AssemblyInfo>> pool, List<AssemblyInfo> result)
			{
				while (pool.Count > 0) {
					var toRemoved = new List<Node<AssemblyInfo>> ();
					foreach (var node in pool) {
						if (node.Parents.Count == 0) {
							toRemoved.Add (node);
							if (result.Contains (node.Item))
								continue;

							result.Add (node.Item);
						}
					}

					foreach (var remove in toRemoved) {
						pool.Remove (remove);
						foreach (var child in remove.Children) {
							if (result.Contains (child.Item))
								continue;

							child.Parents.Remove (remove);
						}
					}
				}
			}
		}

		private void ReorderAssemblies ()
		{
			var graph = new Graph (assemblyList);
			assemblyList.Clear ();
			assemblyList.AddRange (graph.GetOrderedList ());
		}

		/// <summary>
		/// Looks through the settings, trys to make sure everything looks ok.
		/// </summary>
		public void CheckSettings ()
		{
			if (!Directory.Exists (Settings.InPath))
				throw new ObfuscarException ("Path specified by InPath variable must exist:" + Settings.InPath);

			if (!Directory.Exists (Settings.OutPath)) {
				try {
					Directory.CreateDirectory (Settings.OutPath);
				} catch (IOException e) {
					throw new ObfuscarException ("Could not create path specified by OutPath:  " + Settings.OutPath, e);
				}
			}
		}

		internal InheritMap InheritMap {
			get { return inheritMap; }
		}

		internal Settings Settings {
			get {
				if (settings == null)
					settings = new Settings (vars);

				return settings;
			}
		}

		public void LoadAssemblies ()
		{
			// build reference tree
			foreach (AssemblyInfo info in assemblyList) {
				// add self reference...makes things easier later, when
				// we need to go through the member references
				info.ReferencedBy.Add (info);

				// try to get each assembly referenced by this one.  if it's in
				// the map (and therefore in the project), set up the mappings
				foreach (AssemblyNameReference nameRef in info.Definition.MainModule.AssemblyReferences) {
					AssemblyInfo reference;
					if (assemblyMap.TryGetValue (nameRef.Name, out reference)) {
						info.References.Add (reference);
						reference.ReferencedBy.Add (info);
					}
				}
			}

			// make each assembly's list of member refs
			foreach (AssemblyInfo info in assemblyList) {
				info.Init ();
			}

			// build inheritance map
			inheritMap = new InheritMap (this);
			ReorderAssemblies ();
		}

		/// <summary>
		/// Returns whether the project contains a given type.
		/// </summary>
		public bool Contains (TypeReference type)
		{
			string name = type.GetScopeName ();

			return assemblyMap.ContainsKey (name);
		}

		/// <summary>
		/// Returns whether the project contains a given type.
		/// </summary>
		internal bool Contains (TypeKey type)
		{
			return assemblyMap.ContainsKey (type.Scope);
		}

		public TypeDefinition GetTypeDefinition (TypeReference type)
		{
			if (type == null)
				return null;

			TypeDefinition typeDef = type as TypeDefinition;
			if (typeDef == null) {
				string name = type.GetScopeName ();

				AssemblyInfo info;
				if (assemblyMap.TryGetValue (name, out info)) {
					string fullName = type.Namespace + "." + type.Name;
					typeDef = info.Definition.MainModule.GetType (fullName);
				}
			}

			return typeDef;
		}

		IEnumerator IEnumerable.GetEnumerator ()
		{
			return assemblyList.GetEnumerator ();
		}

		public IEnumerator<AssemblyInfo> GetEnumerator ()
		{
			return assemblyList.GetEnumerator ();
		}
	}

    public static class HashSetExtensions
    {
        public static void AddRange<T> (this HashSet<T> set, IEnumerable<T> items)
        {
            foreach (var item in items) {
                set.Add (item);
            }
        }
        public static void RemoveRange<T> (this HashSet<T> set, IEnumerable<T> items)
        {
            foreach (var item in items) {
                set.Remove (item);
            }
        }
    }
}
