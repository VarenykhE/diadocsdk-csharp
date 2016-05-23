#addin "Cake.Git"
#tool "ILMerge"
using Cake.Common.Diagnostics;
using Cake.Git;
using System.Text.RegularExpressions;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var dbgSuffix = (configuration == "Debug" ? "-dbg" : "");

var buildDir = (Directory("./bin") + Directory(configuration)).Path;
var DiadocApiSolutionPath = "./DiadocApi.sln";

const string protobufNetDll = "./packages/protobuf-net.1.0.0.280/lib/protobuf-net.dll";
var packageVersion = ""; 

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
	.Does(() =>
	{
		CleanDirectory(buildDir);
	});

Task("Restore-NuGet-Packages")
	.Does(() =>
	{
		NuGetRestore(DiadocApiSolutionPath);
	});

Task("Build")
	.IsDependentOn("Restore-NuGet-Packages")
	.IsDependentOn("GenerateProtoFiles")
	.Does(() =>
	{
		if(IsRunningOnWindows())
		{
			// Use MSBuild
			MSBuild(DiadocApiSolutionPath, settings => settings.SetConfiguration(configuration));
		}
		else
		{
			// Use XBuild
			XBuild(DiadocApiSolutionPath, settings => settings.SetConfiguration(configuration));
		}
	});

Task("GenerateVersionInfo")
	.Does(context =>
	{
		var majorVersion = 1;
		var clearVersion = GetVersionFromTag() ?? "1.36.1";
		var semanticVersionForNuget = GetSemanticVersionV1(clearVersion);
		var semanticVersion = GetSemanticVersionV2(clearVersion);
		if (!string.IsNullOrEmpty(semanticVersion))
		{
			Information("Version from tag: {0}", semanticVersion);
			var versionParts = semanticVersion.Split('.');
			int.TryParse(versionParts[0], out majorVersion);
		}

		var datetimeNow = DateTime.Now;
		var secondsPart = (long)datetimeNow.TimeOfDay.TotalSeconds;
		var assemblyInfo = new AssemblyInfoSettings
		{
			Version = string.Format("{0}.0.0.0", majorVersion),
			FileVersion = semanticVersionForNuget,
			InformationalVersion = semanticVersion
		};
		packageVersion = assemblyInfo.FileVersion;
		CreateAssemblyInfo("./DiadocApi/Properties/AssemblyVersion.cs", assemblyInfo);
		CreateAssemblyInfo("./Samples/Diadoc.Console/Properties/AssemblyVersion.cs", assemblyInfo);
		CreateAssemblyInfo("./Samples/Diadoc.Samples/Properties/AssemblyVersion.cs", assemblyInfo);
	});

Task("GenerateProtoFiles")
	.IsDependentOn("Restore-NuGet-Packages")
	.Does(() =>
	{
		if (!FileExists(protobufNetDll))
			CopyFileToDirectory(protobufNetDll, "./packages/protobuf-net.1.0.0.280/Tools");
			
		var sourceProtoDir = new DirectoryPath("./proto/").MakeAbsolute(Context.Environment);
		var destinationProtoDir = new DirectoryPath("./DiadocApi/Proto/").MakeAbsolute(Context.Environment);

		var files = GetFiles("./proto/**/*.proto");
		foreach (var file in files)
		{
			var outputFile = file.AppendExtension("cs");
			var relativeFile = sourceProtoDir.GetRelativePath(file);
			var destinationFile = destinationProtoDir.CombineWithFilePath(relativeFile).AppendExtension("cs");
			
			if (FileExists(destinationFile) &&
				System.IO.File.GetLastWriteTime(file.FullPath) < System.IO.File.GetLastWriteTime(destinationFile.FullPath))
			{
				Debug("Skip protogen for file: {0}", file.FullPath);
				continue;
			}
			
			var protogenArguments = new ProcessSettings
			{
				Arguments = string.Format("-i:{0} -o:{1}", file, destinationFile),
				WorkingDirectory = sourceProtoDir 
			};
			
			var exitCode = StartProcess("./packages/protobuf-net.1.0.0.280/Tools/protogen.exe", protogenArguments);
			if (exitCode != 0)
			{
				Error("Error processing file {0} to {1}, protogen exit code: {2}",
					file,
					outputFile,
					exitCode);
			}
		}
	});

Task("ILMerge")
	.IsDependentOn("Build")
	.Does(() =>
	{
		var sourceDir = buildDir.Combine("DiadocApi");
		var outputDir = buildDir.Combine("DiadocApi.Nuget");
		//var keyFile = new FilePath("./DiadocApi/diadoc.snk").MakeAbsolute(Context.Environment).FullPath;
		CreateDirectory(outputDir.Combine("net35"));
		ILMerge(
			outputDir.CombineWithFilePath("net35/DiadocApi.dll"),
			sourceDir.CombineWithFilePath("net35/DiadocApi.dll"),
			new FilePath[] { protobufNetDll },
			new ILMergeSettings
			{
				//ArgumentCustomization = args => args.Append("/keyfile:" + keyFile),
				Internalize = true,
			});
	});

Task("Nuget-Pack")
	.IsDependentOn("GenerateVersionInfo")
	.IsDependentOn("ILMerge")
	.Does(() =>
	{
		var nuGetPackSettings = new NuGetPackSettings
		{
			Version = packageVersion,
			BasePath = buildDir.FullPath,
			OutputDirectory = buildDir.FullPath
		};
		CopyFileToDirectory("./LICENSE.md", nuGetPackSettings.BasePath);
            
		NuGetPack("./nuspec/DiadocApi.nuspec", nuGetPackSettings);
	});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")
	.IsDependentOn("Nuget-Pack");

Task("FullRebuild")
	.IsDependentOn("Clean")
	.IsDependentOn("GenerateVersionInfo")
	.IsDependentOn("Build");

Task("Appveyor")
	.IsDependentOn("FullRebuild")
	.IsDependentOn("ILMerge")
	.IsDependentOn("Nuget-Pack");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);

//////////////////////////////////////////////////////////////////////
// HELPERS
//////////////////////////////////////////////////////////////////////

public string GetVersionFromTag()
{
	var lastestTag = "";

	if (BuildSystem.IsRunningOnAppVeyor)
	{
		var tag = BuildSystem.AppVeyor.Environment.Repository.Tag;
		if (tag.IsTag)
		{
			return ClearVersionTag(tag.Name);
		}
	}
	
	if (string.IsNullOrEmpty(lastestTag))
	{
		try
		{
			var processSettings = new ProcessSettings()
				.SetRedirectStandardOutput(true)
				.WithArguments(x => {
					x.Append("describe")
						.Append("--tags")
						.AppendSwitch("--match", "versions/*")
						.Append("--abbrev=0");
				});
			using (var describeProcess = StartAndReturnProcess("git", processSettings))
			{
				describeProcess.WaitForExit();
				var lines = describeProcess.GetStandardOutput().ToList();
				if (lines.Count == 0 || lines.Count > 1)
				{
					Warning("git describe returns no tags [{0}]", string.Join("\n", lines));
					return null;
				}
				lastestTag = lines.FirstOrDefault();
			}
			// var gitRoot = GitAliases.GitFindRootFromPath(Context, System.IO.Directory.GetCurrentDirectory());
			// lastestTag = GitAliases.GitDescribe(Context, gitRoot, GitDescribeStrategy.Tags);
		}
		catch (Exception ex)
		{
			Warning(ex.Message, new object[] {});
		}
	}
	
	return ClearVersionTag(lastestTag);
}

public string GetSemanticVersionV1(string clearVersion)
{
	if (BuildSystem.IsRunningOnAppVeyor)
	{
		var tag = BuildSystem.AppVeyor.Environment.Repository.Tag;
		if (tag.IsTag)
		{
			return clearVersion;
		}
		
		var buildNumber = BuildSystem.AppVeyor.Environment.Build.Number;
		return string.Format("{0}-CI.{1}", clearVersion, buildNumber);
	}
	
	return string.Format("{0}-dev", clearVersion);
}

public string GetSemanticVersionV2(string clearVersion)
{
	if (BuildSystem.IsRunningOnAppVeyor)
	{
		var tag = BuildSystem.AppVeyor.Environment.Repository.Tag;
		if (tag.IsTag)
		{
			return clearVersion;
		}
		
		var buildNumber = BuildSystem.AppVeyor.Environment.Build.Number;
		clearVersion += string.Format("-CI.{0}-", buildNumber);
		return (AppVeyor.Environment.PullRequest.IsPullRequest
			? clearVersion += string.Format("-PR.{0}", AppVeyor.Environment.PullRequest.Number)
			: clearVersion += "-" + AppVeyor.Environment.Repository.Branch)
			+ dbgSuffix;
	}

	var currentDate = DateTime.Now;
	var daysPart = (currentDate - new DateTime(2010, 01, 01)).Days;
	var secondsPart = Math.Floor((currentDate - currentDate.Date).TotalSeconds/2);
	return string.Format("{0}-dev.{1}.{2}{3}", clearVersion, daysPart, secondsPart, dbgSuffix); 
}

public static string ClearVersionTag(string lastestTag)
{
	if (lastestTag.StartsWith("versions/"))
	{
		lastestTag = lastestTag.Substring("versions/".Length);
	}
	
	var match = Regex.Match(lastestTag, @"^([0-9]+.[0-9]+.[0-9]*)");
	lastestTag = match.Success
		? match.Value
		: lastestTag;
		
	return string.IsNullOrEmpty(lastestTag)
		? null
		: lastestTag;
}