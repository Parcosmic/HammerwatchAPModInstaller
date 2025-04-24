using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using Microsoft.Win32;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Hammerpelago_Installer
{
    class HammerwatchAPInstaller
    {
        const string HAMMERWATCH_EXE_NAME = "Hammerwatch.exe";

        const string SDL2_CS_ORIGINAL_NAME = "SDL2-CS_original.dll";
        const string SDL2_CS_NAME = "SDL2-CS.dll";
        const string MOD_DLL_NAME = "HammerwatchAP.dll";

        const string MONO_CECIL_NAME = "Mono.Cecil.dll";

        [STAThread]
        private static void Main(string[] args)
        {
            Console.WriteLine("Hammerwatch Archipelago Installer");

            string installerPath = Directory.GetCurrentDirectory();
            string hammerwatchPath = null;

            //Check if the installer is in the install directory, if it is stop the program as that method of installation is no longer supported
            if (!File.Exists(HAMMERWATCH_EXE_NAME))
            {
                string initialDirectory = "C:\\";
                try
                {
                    RegistryKey steamKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Wow6432Node\\Valve\\Steam");
                    string steamDir = steamKey.GetValue("InstallPath") as string;
                    StreamReader libraryFoldersReader = File.OpenText(Path.Combine(steamDir, "steamapps", "libraryfolders.vdf"));
                    List<string> paths = new List<string>();
                    //Parse through folders file and keep track of steam install paths
                    while (!libraryFoldersReader.EndOfStream)
                    {
                        string line = libraryFoldersReader.ReadLine();
                        if (line.Contains("\"path\""))
                        {
                            line = line.Replace("\"path\"", "");
                            line = line.Substring(line.IndexOf('"') + 1);
                            line = line.Remove(line.Length - 1);
                            paths.Add(line);
                        }
                    }
                    for (int p = 0; p < paths.Count; p++)
                    {
                        //For some reason games are installed in either "steam" or "steamapps"
                        //Not sure what the distinction is but seems like C:// is "steam" and D:// is "steamapps"?
                        string path1 = Path.Combine(paths[p], "steam\\common\\Hammerwatch");
                        if (Directory.Exists(path1))
                        {
                            initialDirectory = path1;
                            break;
                        }
                        string path2 = Path.Combine(paths[p], "steamapps\\common\\Hammerwatch");
                        if (Directory.Exists(path2))
                        {
                            initialDirectory = path2;
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("An error has occured when trying to find the Hammerwatch install directory:");
                    Console.Error.WriteLine(e.ToString());
                }

                Console.WriteLine("Please select a file in the dialogue window");
                OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    InitialDirectory = initialDirectory,
                    Filter = "Hammerwatch.exe|Hammerwatch.exe",
                    FilterIndex = 1,
                    RestoreDirectory = true
                };

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    hammerwatchPath = Path.GetDirectoryName(openFileDialog.FileName);
                }
                openFileDialog.Dispose();
            }
            else
            {
                Console.Error.WriteLine("Installer located in Hammerwatch install directory");
                Console.Error.WriteLine("This method of installation is no longer supported. Please do not move any files before installing the mod");
                Exit();
                return;
            }

            if (hammerwatchPath != null)
            {
                string hammerwatchExePath = Path.Combine(hammerwatchPath, HAMMERWATCH_EXE_NAME);
                string apAssetsPath = Path.Combine(hammerwatchPath, "archipelago-assets");
                string librariesPath = Path.Combine(hammerwatchPath, "libraries");
                string vanillaLocation = Path.Combine(apAssetsPath, HAMMERWATCH_EXE_NAME);
                //Get hash of the exe to see if it's an old patched version
                try
                {
                    //Open hammerwatch exe path and compute the hash to check if it's vanilla
                    FileStream hammerwatchExeStream = new FileStream(hammerwatchExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    MD5 md5 = MD5.Create();
                    string exeHashString = BitConverter.ToString(md5.ComputeHash(hammerwatchExeStream)).Replace("-", "").ToLower();
                    hammerwatchExeStream.Dispose();
                    md5.Dispose();

                    if (exeHashString != "ddf8414912a48b5b2b77873a66a41b57") //Vanilla hash
                    {
                        if (Directory.Exists(apAssetsPath))
                        {
                            Console.WriteLine("Found old patched version of Hammerwatch, reverting to vanilla");
                            File.Copy(vanillaLocation, hammerwatchExePath, true);
                        }
                        else
                        {
                            Console.Error.WriteLine("Vanilla Hammerwatch exe not found, please reinstall Hammerwatch");
                            Exit();
                            return;
                        }
                    }
                }
                catch (FileNotFoundException ex)
                {
                    Console.Error.WriteLine($"Could not open '{ex.FileName}'.");
                    Console.Error.WriteLine("Make sure all supplied mod files exist in the same directory as the installer");
                    Exit();
                    return;
                }
                //Copy mod files to Hammerwatch directory
                if (Directory.Exists(apAssetsPath))
                {
                    Console.WriteLine("Mod already installed, removing and reinstalling");
                    Directory.Delete(apAssetsPath, true);
                }
                if(Directory.Exists(librariesPath))
                {
                    Directory.Delete(librariesPath, true);
                }
                DeepCopy(installerPath, hammerwatchPath, new List<string>() { "LICENSE.txt", "HammerwatchAPModInstaller.exe", MONO_CECIL_NAME });
                File.Copy(Path.Combine(installerPath, MONO_CECIL_NAME), Path.Combine(hammerwatchPath, "libraries", MONO_CECIL_NAME));
                try
                {
                    string SDL2CSOriginalPath = Path.Combine(hammerwatchPath, SDL2_CS_ORIGINAL_NAME);
                    string SDL2CSPath = Path.Combine(hammerwatchPath, SDL2_CS_NAME);
                    string modDllPath = Path.Combine(hammerwatchPath, MOD_DLL_NAME);
                    if (File.Exists(SDL2CSOriginalPath))
                    {
                        //If this file exists that means we already patched the file previously. Overwrite the patched file so we can patch it again (in case we need to update this in the future)
                        File.Copy(SDL2CSOriginalPath, SDL2CSPath, true);
                    }
                    else
                    {
                        File.Copy(SDL2CSPath, SDL2CSOriginalPath);
                    }
                    AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(SDL2CSOriginalPath);
                    ModuleDefinition module = assembly.MainModule;

                    //Find our main constructor (..ctor) and get the very last instruction (a 'ret', we want to insert right before there)
                    TypeDefinition targetType = null;
                    foreach (TypeDefinition type in module.Types)
                    {
                        if (type.Namespace == "SDL2" && type.Name == "SDL")
                        {
                            targetType = type;
                            break;
                        }
                    }
                    MethodDefinition cctorMethod = null;
                    foreach (MethodDefinition method in targetType.Methods)
                    {
                        if (method.IsConstructor && method.IsStatic)
                        {
                            cctorMethod = method;
                            break;
                        }
                    }
                    ILProcessor processor = cctorMethod.Body.GetILProcessor();
                    Mono.Collections.Generic.Collection<Instruction> instructions = cctorMethod.Body.Instructions;
                    Instruction retInstruction = instructions[instructions.Count - 1];

                    MethodInfo _mi_Assembly_LoadFrom = typeof(Assembly).GetMethod(nameof(Assembly.LoadFrom), BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(string) }, null);
                    MethodReference loadFromRef = module.ImportReference(_mi_Assembly_LoadFrom);

                    AssemblyDefinition hwAssembly = AssemblyDefinition.ReadAssembly(modDllPath);
                    TypeDefinition hwAssemblyType = hwAssembly.MainModule.GetType("HammerwatchAP.HammerwatchAP");
                    MethodDefinition initMethod = hwAssemblyType.Methods[0];
                    MethodReference initRef = module.ImportReference(initMethod);

                    processor.InsertBefore(retInstruction, processor.Create(OpCodes.Ldstr, MOD_DLL_NAME));
                    processor.InsertBefore(retInstruction, processor.Create(OpCodes.Call, loadFromRef));
                    processor.InsertBefore(retInstruction, processor.Create(OpCodes.Pop));
                    processor.InsertBefore(retInstruction, processor.Create(OpCodes.Call, initRef));

                    assembly.Write(SDL2CSPath);
                    Console.WriteLine("Modified assembly saved to: " + SDL2CSPath);
                }
                catch (FileNotFoundException ex)
                {
                    Console.Error.WriteLine($"Could not open '{ex.FileName}'.");
                    Console.Error.WriteLine("Make sure all supplied mod files exist in the same directory as the installer");
                    Exit();
                    return;
                }
                Console.WriteLine("Patching successful!");
            }
            else
            {
                Console.WriteLine("No valid file selected, exiting installation process");
            }

            Exit();
        }

        private static void Exit()
        {
            Console.WriteLine("Press any key to close this window...");
            Console.Read();
        }

        private static void DeepCopy(string fromFolder, string toFolder, List<string> exceptionFiles = null)
        {
            string[] files = Directory.GetFiles(fromFolder);
            Directory.CreateDirectory(toFolder);
            foreach (string file in files)
            {
                if (exceptionFiles != null && exceptionFiles.Contains(Path.GetFileName(file))) continue;
                string dest = Path.Combine(toFolder, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }
            string[] folders = Directory.GetDirectories(fromFolder);
            foreach (string folder in folders)
            {
                DeepCopy(folder, Path.Combine(toFolder, Path.GetFileNameWithoutExtension(folder)));
            }
        }
    }
}
