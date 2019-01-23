using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine;
using Mono.Cecil;

namespace RemoveSpecificVersion {
    static class Program {
        // l "aaaa.dll"
        static void Main(string[] args) {
            if (args.Length == 0)
                args = new string[] {"--help"};
            Parser.Default.ParseArguments<Options>(args)
                  .WithParsed(options => {
                      var fileName = GetFullPath(options.Path);
                      //using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.ReadWrite))
                      //{                     
                      var directory = Path.GetDirectoryName(fileName).ToUpper();

                      var assemblies = AssemblyDefinition
                                       .ReadAssembly(fileName, new ReaderParameters() {ReadWrite = true})
                                       .Yield(current => {
                                           if (!options.Recursive)
                                               return Enumerable.Empty<AssemblyDefinition>();
                                           var references = current.GetReferences(options.Filter, true).references;
                                           List<AssemblyDefinition> result = new List<AssemblyDefinition>();
                                           (current.MainModule.AssemblyResolver as BaseAssemblyResolver)
                                               ?.AddSearchDirectory(directory);
                                           foreach (var reference in references) {
                                               AssemblyDefinition resolved = null;
                                               try {
                                                   resolved = current.MainModule.AssemblyResolver.Resolve(reference);
                                               } catch {
                                                   continue;
                                               }

                                               if (resolved.MainModule.FileName.ToUpper().StartsWith(directory)) {
                                                   //resolved.Dispose();
                                                   //result.Add(current.MainModule.AssemblyResolver.Resolve(reference,
                                                   //    new ReaderParameters() {ReadWrite = true}));
                                                   result.Add(resolved);
                                               }
                                           }

                                           return result;
                                       })
                                       .Distinct()
                                       .Select(x => (x.MainModule.FileName, x))
                                       .Select(x => {
                                           x.Item2.Dispose();
                                           return x.Item1;
                                       })
                                       .Select(x => AssemblyDefinition.ReadAssembly(x, new ReaderParameters() {ReadWrite = true}))
                                       .Select(x => x.GetReferences(options.Filter))
                                       .Where(x => x.references.Any())
                                       .ToArray();

                      foreach (var assembly in assemblies) {
                          (assembly.assembly.MainModule.AssemblyResolver as BaseAssemblyResolver)
                              ?.AddSearchDirectory(directory);
                          var assemblyReferences = assembly
                              .Custom(_ => Console.WriteLine($"Processing {_.assembly.FullName}..."));
                          if (options.List) {
                              assemblyReferences
                                  .Print();
                          }

                          if (options.Patch) {
                              assemblyReferences
                                  .Patch(options.PublicKeyToken, options.Version, options.KillLicx, options.Resources)
                                  .Write();
                          }
                          

                          assembly.assembly.Dispose();
                      }
                      Console.WriteLine("Press any key...");
                      Console.ReadKey();
                  });
        }

        static IEnumerable<T> Yield<T>(this T source, Func<T, IEnumerable<T>> selector) {
            yield return source;
            foreach (var t in selector(source)) {
                foreach (var result in Yield(t, selector)) {
                    yield return result;
                }
            }
        }

        //static IEnumerable<T> Yield
        static (AssemblyDefinition assembly, AssemblyNameReference[] references) GetReferences(
            this AssemblyDefinition definition, string filter, bool except = false) {
            var regex = new Regex(filter);
            return (definition,
                definition.MainModule.AssemblyReferences.Where(x => regex.IsMatch(x.FullName) ? !except : except).ToArray());
        }

        static (AssemblyDefinition assembly, AssemblyNameReference[] references) Print(
            this (AssemblyDefinition assembly, AssemblyNameReference[] references) _this) {
            foreach (var reference in _this.references)
                Console.WriteLine(reference.FullName);
            return _this;
        }

        static (AssemblyDefinition assembly, AssemblyNameReference[] references) Patch(
            this (AssemblyDefinition assembly, AssemblyNameReference[] references) _this,
            string publicKeyToken,
            string version,
            bool killLicx,
            bool patchResources) {
            var main = _this.assembly.MainModule;
            if (killLicx && main.HasResources) {
                var licenses = main.Resources.FirstOrDefault(x => x.Name.ToLower().EndsWith("licenses"));
                if (licenses != null)
                    main.Resources.Remove(licenses);
            }

            byte[] token = String.IsNullOrEmpty(publicKeyToken) ? null : StringToByteArray(publicKeyToken);
            Dictionary<string, string> replacements = new Dictionary<string, string>();
            for (var index = 0; index < _this.references.Length; index++) {
                var reference = _this.references[index];
                var currentName = reference.FullName;
                if (version != null)
                    reference.Version = Version.Parse(version);
                if (token != null)
                    reference.PublicKeyToken = token;
                reference.Culture = null;
                var newName = reference.FullName;
                replacements.Add(currentName, newName);
            }

            if (patchResources && main.HasResources) {
                var targetResource = main.Resources.FirstOrDefault(x => x.Name.ToLower().EndsWith("g.resources")) as EmbeddedResource;
                EmbeddedResource newResource = targetResource;
                if (targetResource != null) {

                    var ms = new MemoryStream();
                    var reader = new ResourceReader(targetResource.GetResourceStream());
                    var writer = new ResourceWriter(ms);
                    foreach (DictionaryEntry entry in reader) {
                        var item = Convert.ToString(entry.Key);
                        reader.GetResourceData(item, out var type, out var rdata);
                        if (Convert.ToString(entry.Key)?.EndsWith(".baml") ?? false) {
                            var asciiStr = Encoding.ASCII.GetString(rdata);
                            foreach (var replacement in replacements) {
                                var index = -1;
                                while ((index = asciiStr.IndexOf(replacement.Key)) != -1) {
                                    asciiStr = asciiStr.Substring(0, index) + replacement.Value + asciiStr.Substring(index + replacement.Key.Length);
                                    var tData = new byte[rdata.Length - replacement.Key.Length + replacement.Value.Length];
                                    Array.Copy(rdata, tData, index);
                                    var lastIndex = index;
                                    Array.Copy(Encoding.ASCII.GetBytes(replacement.Value), 0, tData, index, replacement.Value.Length);
                                    lastIndex += replacement.Value.Length;
                                    Array.Copy(rdata, index + replacement.Key.Length, tData, lastIndex, tData.Length - lastIndex);
                                    rdata = tData;
                                }
                            }
                            
                        }
                        writer.AddResourceData(item, type, rdata);
                    }                    
                    writer.Generate();                   

                    newResource = new EmbeddedResource(targetResource.Name, targetResource.Attributes, ms.GetBuffer());

                    var resourceindex = main.Resources.IndexOf(targetResource);
                    main.Resources.RemoveAt(resourceindex);
                    main.Resources[resourceindex] = newResource;
                }
            }

            return _this;
        }            

        static T Custom<T>(this T _this, Action<T> callback) {
            callback(_this);
            return _this;
        }

        static void Write(    
            this (AssemblyDefinition assembly, AssemblyNameReference[] references) _this) {
            _this.assembly.Write();
        }

        private static string GetFullPath(string path) {
            var fileName = path;
            if (!Path.IsPathRooted(fileName)) {
                var dir = Directory.GetCurrentDirectory();
                fileName = Path.Combine(dir, fileName);
            }

            return fileName;
        }

        public static byte[] StringToByteArray(string hex) {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }
    }

    public class Options {
        [Option('l', "list", HelpText = "List References")]
        public bool List { get; set; }

        [Value(0, HelpText = "Path to an assembly", Hidden = true)]
        public string Path { get; set; }

        [Option('f', "filter", HelpText = "Assembly Filter (RegEx)", Default = "DevExpress.*")]
        public string Filter { get; set; }

        [Option('p', "patch")]
        public bool Patch { get; set; }

        [Option('k', "key", HelpText = "Public key token")]
        public string PublicKeyToken { get; set; }

        [Option('v', "version", HelpText = "Version")]
        public string Version { get; set; }

        [Option('r', "recursive", HelpText = "Recursive", Default = true)]
        public bool Recursive { get; set; }
        [Option('k', "licx", HelpText = "Kill licenses.licx", Default = true)]
        public bool KillLicx { get; set; }
        [Option('r', "resources", HelpText = "UpdateResourceInformation", Default=true)]
        public bool Resources { get; set; }
    }
}