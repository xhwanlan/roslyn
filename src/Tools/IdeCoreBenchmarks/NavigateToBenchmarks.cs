﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AnalyzerRunner;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.NavigateTo;
using Microsoft.CodeAnalysis.Storage;

namespace IdeCoreBenchmarks
{
    [MemoryDiagnoser]
    public class NavigateToBenchmarks
    {
        [Benchmark]
        public async Task RunNavigateTo()
        {
            try
            {
                // QueryVisualStudioInstances returns Visual Studio installations on .NET Framework, and .NET Core SDK
                // installations on .NET Core. We use the one with the most recent version.
                var msBuildInstance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(x => x.Version).First();

                MSBuildLocator.RegisterInstance(msBuildInstance);

                var roslynRoot = Environment.GetEnvironmentVariable(Program.RoslynRootPathEnvVariableName);
                var solutionPath = Path.Combine(roslynRoot, @"C:\github\roslyn\Roslyn.sln");

                if (!File.Exists(solutionPath))
                    throw new ArgumentException("Couldn't find Roslyn.sln");

                Console.Write("Found Roslyn.sln: " + Process.GetCurrentProcess().Id);

                var assemblies = MSBuildMefHostServices.DefaultAssemblies
                    .Add(typeof(AnalyzerRunnerHelper).Assembly)
                    .Add(typeof(FindReferencesBenchmarks).Assembly);
                var services = MefHostServices.Create(assemblies);

                var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
                {
                    // Use the latest language version to force the full set of available analyzers to run on the project.
                    { "LangVersion", "9.0" },
                }, services);

                if (workspace == null)
                    throw new ArgumentException("Couldn't create workspace");

                workspace.TryApplyChanges(workspace.CurrentSolution.WithOptions(workspace.Options
                    .WithChangedOption(StorageOptions.Database, StorageDatabase.SQLite)));

                Console.WriteLine("Opening roslyn.  Attach to: " + Process.GetCurrentProcess().Id);

                var start = DateTime.Now;
                var solution = workspace.OpenSolutionAsync(solutionPath, progress: null, CancellationToken.None).Result;
                Console.WriteLine("Finished opening roslyn: " + (DateTime.Now - start));

                // Force a storage instance to be created.  This makes it simple to go examine it prior to any operations we
                // perform, including seeing how big the initial string table is.
                var storageService = workspace.Services.GetPersistentStorageService(workspace.CurrentSolution.Options);
                if (storageService == null)
                    throw new ArgumentException("Couldn't get storage service");

                using (var storage = await storageService.GetStorageAsync(SolutionKey.ToSolutionKey(workspace.CurrentSolution), CancellationToken.None))
                {
                    Console.WriteLine();
                }

                Console.WriteLine("Starting navigate to");

                start = DateTime.Now;
                // Search each project with an independent threadpool task.
                var searchTasks = solution.Projects.Select(
                    p => Task.Run(() => SearchAsync(p, priorityDocuments: ImmutableArray<Document>.Empty), CancellationToken.None)).ToArray();

                var result = await Task.WhenAll(searchTasks).ConfigureAwait(false);
                var sum = result.Sum();

                start = DateTime.Now;
                Console.WriteLine("Num results: " + (DateTime.Now - start));
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var ex2 in ex.LoaderExceptions)
                    Console.WriteLine(ex2);
            }
        }

        private async Task<int> SearchAsync(Project project, ImmutableArray<Document> priorityDocuments)
        {
            var service = project.LanguageServices.GetService<INavigateToSearchService>();
            var results = new List<INavigateToSearchResult>();
            await service.SearchProjectAsync(
                project, priorityDocuments, "Document", service.KindsProvided,
                r =>
                {
                    lock (results)
                        results.Add(r);

                    return Task.CompletedTask;
                }, isFullyLoaded: true, CancellationToken.None);

            return results.Count;
        }
    }
}
