using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Npm;
using Nuke.Common.Utilities.Collections;
using System;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.Docker.DockerTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Npm.NpmTasks;
using static Nuke.GitHub.GitHubTasks;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using System.Linq;
using Nuke.GitHub;

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [GitVersion(Framework = "net7.0")] GitVersion GitVersion;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath OutputDirectory => RootDirectory / "output";
    AbsolutePath ChangeLogFile => RootDirectory / "CHANGELOG.md";
    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;

    readonly string DockerImageName = "dangl-schneid-control";

    [Parameter] readonly string DockerRegistryUrl;
    [Parameter] readonly string DockerRegistryUsername;
    [Parameter] readonly string DockerRegistryPassword;
    [Parameter] readonly string PublicDockerRegistryUsername;
    [Parameter] readonly string PublicDockerRegistryPassword;
    [Parameter] readonly string PublicDockerOrganization = "dangl";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            OutputDirectory.CreateOrCleanDirectory();
        });

    Target GenerateVersion => _ => _
        .Executes(() =>
        {
            var buildDate = DateTime.UtcNow;

            var filePath = SourceDirectory / "Dangl.SchneidControl" / "VersionsService.cs";

            var currentDateUtc = $"new DateTime({buildDate.Year}, {buildDate.Month}, {buildDate.Day}, {buildDate.Hour}, {buildDate.Minute}, {buildDate.Second}, DateTimeKind.Utc)";

            var content = $@"using System;

namespace Dangl.SchneidControl
{{
    // This file is automatically generated
    [System.CodeDom.Compiler.GeneratedCode(""GitVersionBuild"", """")]
    public static class VersionsService
    {{
        public static string Version => ""{GitVersion.NuGetVersionV2}"";
        public static string CommitInfo => ""{GitVersion.FullBuildMetaData}"";
        public static string CommitDate => ""{GitVersion.CommitDate}"";
        public static string CommitHash => ""{GitVersion.Sha}"";
        public static string InformationalVersion => ""{GitVersion.InformationalVersion}"";
        public static DateTime BuildDateUtc {{ get; }} = {currentDateUtc};
    }}
}}";
            filePath.WriteAllText(content);
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProcessArgumentConfigurator(a => a.Add("/nodeReuse:false"))
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .DependsOn(GenerateVersion)
        .Executes(() =>
        {
            CompileBackend();
        });

    void CompileBackend()
    {
        DotNetBuild(s => s
            .SetProcessArgumentConfigurator(a => a.Add("/nodeReuse:false"))
            .SetProjectFile(Solution)
            .SetConfiguration(Configuration)
            .SetAssemblyVersion(GitVersion.AssemblySemVer)
            .SetFileVersion(GitVersion.AssemblySemFileVer)
            .SetInformationalVersion(GitVersion.InformationalVersion)
            .EnableNoRestore());
    }

    Target BuildFrontendSwaggerClient => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            CompileBackend(); // Need that with newer NSwag so the project outputs are present
            var nSwagConfigPath = SourceDirectory / "dangl-schneid-control" / "src" / "nswag.json";
            var nSwagToolPath = NuGetToolPathResolver.GetPackageExecutable("NSwag.MSBuild", "tools/Net80/dotnet-nswag.dll");

            DotNetRun(x => x
                .SetProcessToolPath(nSwagToolPath)
                .SetProcessWorkingDirectory(SourceDirectory / "dangl-schneid-control" / "src")
                .SetProcessArgumentConfigurator(y => y
                    .Add(nSwagConfigPath)));
        });

    Target FrontEndRestore => _ => _
        .After(Clean)
        .Executes(() =>
        {
            (SourceDirectory / "dangl-schneid-control" / "node_modules").CreateOrCleanDirectory();
            (SourceDirectory / "dangl-schneid-control" / "node_modules").DeleteDirectory();
            Npm("ci", SourceDirectory / "dangl-schneid-control");
        });

    Target BuildFrontend => _ => _
        .DependsOn(BuildFrontendSwaggerClient)
        .DependsOn(GenerateVersion)
        .DependsOn(FrontEndRestore)
        .Executes(() =>
        {
            (SourceDirectory / "Dangl.SchneidControl" / "wwwroot" / "dist").CreateOrCleanDirectory();

            NpmRun(x => x
                .SetCommand("build:prod")
                .SetProcessWorkingDirectory(SourceDirectory / "dangl-schneid-control"));
        });

    Target BuildDocker => _ => _
        .DependsOn(Restore)
        .DependsOn(GenerateVersion)
        .DependsOn(BuildFrontend)
        .Requires(() => Configuration == "Release")
        .Executes(() =>
        {
            DotNetPublish(s => s
                .SetProcessArgumentConfigurator(a => a.Add("/nodeReuse:false"))
                .SetProject(SourceDirectory / "Dangl.SchneidControl")
                .SetOutput(OutputDirectory)
                .SetConfiguration(Configuration));

            foreach (var configFileToDelete in OutputDirectory.GlobFiles("web*.config"))
            {
                configFileToDelete.DeleteFile();
            }

            (SourceDirectory / "Dangl.SchneidControl" / "Dockerfile").Copy(OutputDirectory / "Dockerfile", ExistsPolicy.FileOverwrite);

            DockerBuild(c => c
                .SetFile(OutputDirectory / "Dockerfile")
                .SetTag(DockerImageName + ":dev")
                .SetPath(".")
                .SetPull(true)
                .SetProcessWorkingDirectory(OutputDirectory));

            OutputDirectory.CreateOrCleanDirectory();
        });

    Target PushDocker => _ => _
        .DependsOn(BuildDocker)
        .Requires(() => DockerRegistryUrl)
        .Requires(() => DockerRegistryUsername)
        .Requires(() => DockerRegistryPassword)
        .OnlyWhenDynamic(() => IsOnBranch("main") || IsOnBranch("develop"))
        .Executes(() =>
        {
            DockerLogin(x => x
                .SetUsername(DockerRegistryUsername)
                .SetServer(DockerRegistryUrl.ToLowerInvariant())
                .SetPassword(DockerRegistryPassword)
                .DisableProcessLogOutput());

            PushDockerWithTag("dev", DockerRegistryUrl, DockerImageName);

            if (IsOnBranch("main"))
            {
                PushDockerWithTag("latest", DockerRegistryUrl, DockerImageName);
                PushDockerWithTag(GitVersion.SemVer, DockerRegistryUrl, DockerImageName);
            }

            if (!string.IsNullOrWhiteSpace(PublicDockerRegistryUsername)
                && !string.IsNullOrWhiteSpace(PublicDockerRegistryPassword)
                && !string.IsNullOrWhiteSpace(PublicDockerOrganization))
            {
                DockerLogin(x => x
                    .SetUsername(PublicDockerRegistryUsername)
                    .SetPassword(PublicDockerRegistryPassword)
                    .DisableProcessLogOutput());

                PushPublicDockerWithTag("dev", DockerImageName);

                if (IsOnBranch("main"))
                {
                    PushPublicDockerWithTag("latest", DockerImageName);
                    PushPublicDockerWithTag(GitVersion.SemVer, DockerImageName);
                }
            }
        });

    private void PushDockerWithTag(string tag, string dockerRegistryUrl, string targetDockerImageName)
    {
        DockerTag(c => c
            .SetSourceImage(DockerImageName + ":" + "dev")
            .SetTargetImage($"{dockerRegistryUrl}/{targetDockerImageName}:{tag}".ToLowerInvariant()));
        DockerPush(c => c
            .SetName($"{dockerRegistryUrl}/{targetDockerImageName}:{tag}".ToLowerInvariant()));
    }

    private void PushPublicDockerWithTag(string tag, string targetDockerImageName)
    {
        DockerTag(c => c
            .SetSourceImage(DockerImageName + ":" + "dev")
            .SetTargetImage($"{PublicDockerOrganization}/{targetDockerImageName}:{tag}".ToLowerInvariant()));
        DockerPush(c => c
            .SetName($"{PublicDockerOrganization}/{targetDockerImageName}:{tag}".ToLowerInvariant()));
    }

    private bool IsOnBranch(string branchName)
    {
        return GitVersion.BranchName.Equals(branchName) || GitVersion.BranchName.Equals($"origin/{branchName}");
    }

    Target PublishGitHubRelease => _ => _
         .OnlyWhenDynamic(() => IsOnBranch("main"))
         .After(PushDocker)
         .Executes(async () =>
         {
             Assert.NotNull(GitHubActions.Instance?.Token);
             var releaseTag = $"v{GitVersion.MajorMinorPatch}";

             var changeLogSectionEntries = ExtractChangelogSectionNotes(ChangeLogFile);
             var latestChangeLog = changeLogSectionEntries
                 .Aggregate((c, n) => c + Environment.NewLine + n);
             var completeChangeLog = $"## {releaseTag}" + Environment.NewLine + latestChangeLog;

             var repositoryInfo = GetGitHubRepositoryInfo(GitRepository);

             await PublishRelease(x => x
                     .SetCommitSha(GitVersion.Sha)
                     .SetReleaseNotes(completeChangeLog)
                     .SetRepositoryName(repositoryInfo.repositoryName)
                     .SetRepositoryOwner(repositoryInfo.gitHubOwner)
                     .SetTag(releaseTag)
                     .SetToken(GitHubActions.Instance.Token));
         });
}
