﻿// Copyright (C) 2011 Xamarin, Inc. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Xamarin.Android.Tools;
using ThreadingTasks = System.Threading.Tasks;

namespace Xamarin.Android.Tasks
{
	public class Aapt : AsyncTask
	{
		public ITaskItem[] AdditionalAndroidResourcePaths { get; set; }

		public string AndroidComponentResgenFlagFile { get; set; }

		public bool NonConstantId { get; set; }

		public string AssetDirectory { get; set; }

		[Required]
		public ITaskItem[] ManifestFiles { get; set; }

		[Required]
		public string ResourceDirectory { get; set; }

		public string ResourceOutputFile { get; set; }

		[Required]
		public string JavaDesignerOutputDirectory { get; set; }

		[Required]
		public string JavaPlatformJarPath { get; set; }

		public string UncompressedFileExtensions { get; set; }
		public string PackageName { get; set; }

		[Required]
		public string ApplicationName { get; set; }

		public string ExtraPackages { get; set; }

		public ITaskItem [] AdditionalResourceDirectories { get; set; }

		public ITaskItem [] LibraryProjectJars { get; set; }

		public string ExtraArgs { get; set; }

		protected string ToolName { get { return OS.IsWindows ? "aapt.exe" : "aapt"; } }

		public string ToolPath { get; set; }

		public string ToolExe { get; set; }

		public string ApiLevel { get; set; }

		public bool AndroidUseLatestPlatformSdk { get; set; }

		public string SupportedAbis { get; set; }

		public bool CreatePackagePerAbi { get; set; }

		public string ImportsDirectory { get; set; }
		public string OutputImportDirectory { get; set; }
		public bool UseShortFileNames { get; set; }
		public string AssemblyIdentityMapFile { get; set; }

		public string ResourceNameCaseMap { get; set; }

		public bool ExplicitCrunch { get; set; }

		// pattern to use for the version code. Used in CreatePackagePerAbi
		// eg. {abi:00}{dd}{version}
		// known keyworks
		//  {abi} the value for the current abi
		//  {version} the version code from the manifest.
		public string VersionCodePattern { get; set; }

		// Name=Value pair seperated by ';'
		// e.g screen=21;abi=11
		public string VersionCodeProperties { get; set; }

		public string AndroidSdkPlatform { get; set; }

		Dictionary<string,string> resource_name_case_map = new Dictionary<string,string> ();
		AssemblyIdentityMap assemblyMap = new AssemblyIdentityMap ();

		bool ManifestIsUpToDate (string manifestFile)
		{
			return !String.IsNullOrEmpty (AndroidComponentResgenFlagFile) &&
				File.Exists (AndroidComponentResgenFlagFile) && File.Exists (manifestFile) &&
				File.GetLastWriteTime (AndroidComponentResgenFlagFile) > File.GetLastWriteTime (manifestFile);
		}

		bool RunAapt (string commandLine)
		{
			var stdout_completed = new ManualResetEvent (false);
			var stderr_completed = new ManualResetEvent (false);
			var psi = new ProcessStartInfo () {
				FileName = GenerateFullPathToTool (),
				Arguments = commandLine,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
			};

			using (var proc = new Process ()) {
				proc.OutputDataReceived += (sender, e) => {
					if (e.Data != null)
						LogEventsFromTextOutput (e.Data, MessageImportance.Normal);
					else
						stdout_completed.Set ();
				};
				proc.ErrorDataReceived += (sender, e) => {
					if (e.Data != null)
						LogEventsFromTextOutput (e.Data, MessageImportance.Normal);
					else
						stderr_completed.Set ();
				};
				proc.StartInfo = psi;
				proc.Start ();
				proc.BeginOutputReadLine ();
				proc.BeginErrorReadLine ();
				Token.Register (() => {
					try {
						proc.Kill ();
					} catch (Exception) {
					}
				});
				LogDebugMessage ("Executing {0}", commandLine);
				proc.WaitForExit ();
				if (psi.RedirectStandardError)
					stderr_completed.WaitOne (TimeSpan.FromSeconds (30));
				if (psi.RedirectStandardOutput)
					stdout_completed.WaitOne (TimeSpan.FromSeconds (30));
				return proc.ExitCode == 0;
			}
		}

		bool ExecuteForAbi (string cmd, string currentResourceOutputFile)
		{
			var ret = RunAapt (cmd);
			if (ret && !string.IsNullOrEmpty (currentResourceOutputFile)) {
				var tmpfile = currentResourceOutputFile + ".bk";
				MonoAndroidHelper.CopyIfZipChanged (tmpfile, currentResourceOutputFile);
				File.Delete (tmpfile);
			}
			return ret;
		}

		int DoExecute (ITaskItem manifestFile, ThreadingTasks.ParallelLoopState state, int loop)
		{
			if (!File.Exists (manifestFile.ItemSpec)) {
				LogDebugMessage ("{0} does not exists. Skipping", manifestFile.ItemSpec);
				return 0;
			}

			bool upToDate = ManifestIsUpToDate (manifestFile.ItemSpec);

			if (AdditionalAndroidResourcePaths != null)
				foreach (var dir in AdditionalAndroidResourcePaths)
					if (!string.IsNullOrEmpty (dir.ItemSpec))
						upToDate = upToDate && ManifestIsUpToDate (string.Format ("{0}{1}{2}{3}{4}", dir, Path.DirectorySeparatorChar, "manifest", Path.DirectorySeparatorChar, "AndroidManifest.xml"));

			if (upToDate) {
				LogMessage ("  Additional Android Resources manifsets files are unchanged. Skipping.");
				return 0;
			}

			var defaultAbi = new string [] { null };
			var abis = SupportedAbis?.Split (new char [] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (var abi in (CreatePackagePerAbi && abis?.Length > 1) ? defaultAbi.Concat (abis) : defaultAbi) {
				var currentResourceOutputFile = abi != null ? string.Format ("{0}-{1}", ResourceOutputFile, abi) : ResourceOutputFile;
				if (!ExecuteForAbi (GenerateCommandLineCommands (manifestFile.ItemSpec, abi, currentResourceOutputFile), currentResourceOutputFile)) {
					Cancel ();
				}
			}

			return 0;
		}

		public override bool Execute ()
		{
			Log.LogDebugMessage ("Aapt Task");
			Log.LogDebugMessage ("  AssetDirectory: {0}", AssetDirectory);
			Log.LogDebugTaskItems ("  ManifestFiles: ", ManifestFiles);
			Log.LogDebugMessage ("  ResourceDirectory: {0}", ResourceDirectory);
			Log.LogDebugMessage ("  JavaDesignerOutputDirectory: {0}", JavaDesignerOutputDirectory);
			Log.LogDebugMessage ("  PackageName: {0}", PackageName);
			Log.LogDebugMessage ("  UncompressedFileExtensions: {0}", UncompressedFileExtensions);
			Log.LogDebugMessage ("  ExtraPackages: {0}", ExtraPackages);
			Log.LogDebugTaskItems ("  AdditionalResourceDirectories: ", AdditionalResourceDirectories);
			Log.LogDebugTaskItems ("  AdditionalAndroidResourcePaths: ", AdditionalAndroidResourcePaths);
			Log.LogDebugTaskItems ("  LibraryProjectJars: ", LibraryProjectJars);
			Log.LogDebugMessage ("  ExtraArgs: {0}", ExtraArgs);
			Log.LogDebugMessage ("  CreatePackagePerAbi: {0}", CreatePackagePerAbi);
			Log.LogDebugMessage ("  ResourceNameCaseMap: {0}", ResourceNameCaseMap);
			Log.LogDebugMessage ("  VersionCodePattern: {0}", VersionCodePattern);
			Log.LogDebugMessage ("  VersionCodeProperties: {0}", VersionCodeProperties);
			if (CreatePackagePerAbi)
				Log.LogDebugMessage ("  SupportedAbis: {0}", SupportedAbis);

			if (ResourceNameCaseMap != null)
				foreach (var arr in ResourceNameCaseMap.Split (';').Select (l => l.Split ('|')).Where (a => a.Length == 2))
					resource_name_case_map [arr [1]] = arr [0]; // lowercase -> original

			assemblyMap.Load (AssemblyIdentityMapFile);

			ThreadingTasks.ParallelOptions options = new ThreadingTasks.ParallelOptions {
				CancellationToken = Token,
				TaskScheduler = ThreadingTasks.TaskScheduler.Current,
			};

			ThreadingTasks.Parallel.ForEach (ManifestFiles, options, () => 0, DoExecute, (obj) => { Complete (); });

			base.Execute ();

			return !Log.HasLoggedErrors;
		}

		protected string GenerateCommandLineCommands (string ManifestFile, string currentAbi, string currentResourceOutputFile)
		{
			// For creating Resource.Designer.cs:
			//   Running command: C:\Program Files (x86)\Android\android-sdk-windows\platform-tools\aapt
			//     "package"
			//     "-M" "C:\Users\Jonathan\AppData\Local\Temp\ryob4gaw.way\AndroidManifest.xml"
			//     "-J" "C:\Users\Jonathan\AppData\Local\Temp\ryob4gaw.way"
			//     "-F" "C:\Users\Jonathan\AppData\Local\Temp\ryob4gaw.way\resources.apk"
			//     "-S" "c:\users\jonathan\documents\visual studio 2010\Projects\MonoAndroidApplication4\MonoAndroidApplication4\obj\Debug\res"
			//     "-I" "C:\Program Files (x86)\Android\android-sdk-windows\platforms\android-8\android.jar"
			//     "--max-res-version" "10"

			// For packaging:
			//   Running command: C:\Program Files (x86)\Android\android-sdk-windows\platform-tools\aapt
			//     "package"
			//     "-f"
			//     "-m"
			//     "-M" "AndroidManifest.xml"
			//     "-J" "src"
			//     "--custom-package" "androidmsbuildtest.androidmsbuildtest"
			//     "-F" "bin\packaged_resources"
			//     "-S" "C:\Users\Jonathan\Documents\Visual Studio 2010\Projects\AndroidMSBuildTest\AndroidMSBuildTest\obj\Debug\res"
			//     "-I" "C:\Program Files (x86)\Android\android-sdk-windows\platforms\android-8\android.jar"
			//     "--extra-packages" "com.facebook.android:my.another.library"

			var cmd = new CommandLineBuilder ();

			cmd.AppendSwitch ("package");

			if (MonoAndroidHelper.LogInternalExceptions)
				cmd.AppendSwitch ("-v");
			if (NonConstantId)
				cmd.AppendSwitch ("--non-constant-id");
			cmd.AppendSwitch ("-f");
			cmd.AppendSwitch ("-m");
			string manifestFile;
			string manifestDir = Path.Combine (Path.GetDirectoryName (ManifestFile), currentAbi != null ? currentAbi : "manifest");
			Directory.CreateDirectory (manifestDir);
			manifestFile = Path.Combine (manifestDir, Path.GetFileName (ManifestFile));
			ManifestDocument manifest = new ManifestDocument (ManifestFile, this.Log);
			manifest.SdkVersion = AndroidSdkPlatform;
			if (currentAbi != null) {
				if (!string.IsNullOrEmpty (VersionCodePattern))
					manifest.CalculateVersionCode (currentAbi, VersionCodePattern, VersionCodeProperties);
				else
					manifest.SetAbi (currentAbi);
			} else if (!string.IsNullOrEmpty (VersionCodePattern)) {
				manifest.CalculateVersionCode (null, VersionCodePattern, VersionCodeProperties);
			}
			manifest.ApplicationName = ApplicationName;
			manifest.Save (manifestFile);

			cmd.AppendSwitchIfNotNull ("-M ", manifestFile);
			Directory.CreateDirectory (JavaDesignerOutputDirectory);
			cmd.AppendSwitchIfNotNull ("-J ", JavaDesignerOutputDirectory);

			if (PackageName != null)
				cmd.AppendSwitchIfNotNull ("--custom-package ", PackageName.ToLowerInvariant ());

			if (!string.IsNullOrEmpty (currentResourceOutputFile))
				cmd.AppendSwitchIfNotNull ("-F ", currentResourceOutputFile + ".bk");
			// The order of -S arguments is *important*, always make sure this one comes FIRST
			cmd.AppendSwitchIfNotNull ("-S ", ResourceDirectory.TrimEnd ('\\'));
			if (AdditionalResourceDirectories != null)
				foreach (var resdir in AdditionalResourceDirectories)
					cmd.AppendSwitchIfNotNull ("-S ", resdir.ItemSpec.TrimEnd ('\\'));
			if (AdditionalAndroidResourcePaths != null)
				foreach (var dir in AdditionalAndroidResourcePaths)
					cmd.AppendSwitchIfNotNull ("-S ", Path.Combine (dir.ItemSpec.TrimEnd (System.IO.Path.DirectorySeparatorChar), "res"));

			if (LibraryProjectJars != null)
				foreach (var jar in LibraryProjectJars)
					cmd.AppendSwitchIfNotNull ("-j ", jar);
			
			cmd.AppendSwitchIfNotNull ("-I ", JavaPlatformJarPath);

			// Add asset directory if it exists
			if (!string.IsNullOrWhiteSpace (AssetDirectory) && Directory.Exists (AssetDirectory))
				cmd.AppendSwitchIfNotNull ("-A ", AssetDirectory.TrimEnd ('\\'));

			if (!string.IsNullOrWhiteSpace (UncompressedFileExtensions))
				foreach (var ext in UncompressedFileExtensions.Split (new char[] { ';', ','}, StringSplitOptions.RemoveEmptyEntries))
					cmd.AppendSwitchIfNotNull ("-0 ", ext);

			if (!string.IsNullOrEmpty (ExtraPackages))
				cmd.AppendSwitchIfNotNull ("--extra-packages ", ExtraPackages);

			// TODO: handle resource names
			if (ExplicitCrunch)
				cmd.AppendSwitch ("--no-crunch");

			cmd.AppendSwitch ("--auto-add-overlay");

			var extraArgsExpanded = ExpandString (ExtraArgs);
			if (extraArgsExpanded != ExtraArgs)
				Log.LogDebugMessage ("  ExtraArgs expanded: {0}", extraArgsExpanded);

			if (!string.IsNullOrWhiteSpace (extraArgsExpanded))
				cmd.AppendSwitch (extraArgsExpanded);

			if (!AndroidUseLatestPlatformSdk)
				cmd.AppendSwitchIfNotNull ("--max-res-version ", ApiLevel);

			return cmd.ToString ();
		}

		string ExpandString (string s)
		{
			if (s == null)
				return null;
			int start = 0;
			int st = s.IndexOf ("${library.imports:", start, StringComparison.Ordinal);
			if (st >= 0) {
				int ed = s.IndexOf ('}', st);
				if (ed < 0)
					return s.Substring (0, st + 1) + ExpandString (s.Substring (st + 1));
				int ast = st + "${library.imports:".Length;
				string aname = s.Substring (ast, ed - ast);
				return s.Substring (0, st) + Path.Combine (OutputImportDirectory, UseShortFileNames ? assemblyMap.GetLibraryImportDirectoryNameForAssembly (aname) : aname, ImportsDirectory) + Path.DirectorySeparatorChar + ExpandString (s.Substring (ed + 1));
			}
			else
				return s;
		}

		protected string GenerateFullPathToTool ()
		{
			return Path.Combine (ToolPath, string.IsNullOrEmpty (ToolExe) ? ToolName : ToolExe);
		}

		protected void LogEventsFromTextOutput (string singleLine, MessageImportance messageImportance)
		{
			if (string.IsNullOrEmpty (singleLine)) 
				return;

			var match = AndroidToolTask.AndroidErrorRegex.Match (singleLine.Trim ());

			if (match.Success) {
				var file = match.Groups["file"].Value;
				int line = 0;
				if (!string.IsNullOrEmpty (match.Groups["line"]?.Value))
					line = int.Parse (match.Groups["line"].Value) + 1;
				var level = match.Groups["level"].Value;
				var message = match.Groups ["message"].Value;
				if (message.Contains ("fakeLogOpen") || level.Contains ("warning")) {
					LogWarning (singleLine);
					return;
				}

				// Try to map back to the original resource file, so when the user
				// double clicks the error, it won't take them to the obj/Debug copy
				if (file.StartsWith (ResourceDirectory, StringComparison.InvariantCultureIgnoreCase)) {
					file = file.Substring (ResourceDirectory.Length);
					file = resource_name_case_map.ContainsKey (file) ? resource_name_case_map [file] : file;
					file = Path.Combine ("Resources", file);
				}

				// Strip any "Error:" text from aapt's output
				if (message.StartsWith ("error: ", StringComparison.InvariantCultureIgnoreCase))
					message = message.Substring ("error: ".Length);

				LogError ("APT0000", message, file, line);
				return;
			}

			// Handle additional error that doesn't match the regex
			if (singleLine.Trim ().StartsWith ("invalid resource directory name:")) {
				LogError ("APT0000", string.Format ("Invalid resource directory name: \"{0}\".", singleLine.Substring (singleLine.LastIndexOfAny (new char[] { '\\', '/' }) + 1)), ToolName);
				return;
			}

			LogMessage (singleLine, messageImportance);
		}
	}
}
