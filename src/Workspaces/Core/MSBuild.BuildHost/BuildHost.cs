﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.MSBuild.Build;
using Roslyn.Utilities;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost;

internal sealed class BuildHost : IBuildHost
{
    private readonly JsonRpc _jsonRpc;
    private readonly string? _binaryLogPath;

    private readonly object _gate = new object();
    private ProjectFileLoaderRegistry? _projectFileLoaderRegistry;
    private ProjectBuildManager? _buildManager;

    public BuildHost(JsonRpc jsonRpc, string? binaryLogPath)
    {
        _jsonRpc = jsonRpc;
        _binaryLogPath = binaryLogPath;
    }

    [MemberNotNull(nameof(_projectFileLoaderRegistry), nameof(_buildManager))]
    private void EnsureMSBuildLoaded(string projectFilePath)
    {
        lock (_gate)
        {
            // If we've already created our MSBuild types, then there's nothing further to do.
            if (_buildManager != null && _projectFileLoaderRegistry != null)
                return;

#if NETFRAMEWORK

            // In this case, we're just going to pick the highest VS install on the machine, in case the projects are using some newer
            // MSBuild features. Since we don't have something like a global.json we can't really know what the minimum version is.

            // TODO: we should also check that the managed tools are actually installed
            MSBuildLocator.RegisterInstance(MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(vs => vs.Version).First());

#else

            // Locate the right SDK for this particular project; MSBuildLocator ensures in this case the first one is the preferred one.
            // TODO: we should pick the appropriate instance back in the main process and just use the one chosen here.
            var options = new VisualStudioInstanceQueryOptions { DiscoveryTypes = DiscoveryType.DotNetSdk, WorkingDirectory = Path.GetDirectoryName(projectFilePath) };
            MSBuildLocator.RegisterInstance(MSBuildLocator.QueryVisualStudioInstances(options).First());

#endif

            CreateBuildManager();
        }
    }

    [MemberNotNull(nameof(_projectFileLoaderRegistry), nameof(_buildManager))]
    [MethodImpl(MethodImplOptions.NoInlining)] // Do not inline this, since this creates MSBuild types which are being loaded by the caller
    private void CreateBuildManager()
    {
        Contract.ThrowIfFalse(Monitor.IsEntered(_gate));

        var hostServices = MefHostServices.Create(MefHostServices.DefaultAssemblies.Append(typeof(BuildHost).Assembly));
        var solutionServices = new AdhocWorkspace(hostServices).Services.SolutionServices;

        _projectFileLoaderRegistry = new ProjectFileLoaderRegistry(solutionServices, new DiagnosticReporter(new AdhocWorkspace()));

        var logger = _binaryLogPath != null ? new BinaryLogger { Parameters = _binaryLogPath } : null;
        _buildManager = new ProjectBuildManager(ImmutableDictionary<string, string>.Empty, logger);
        _buildManager.StartBatchBuild();
    }

    public Task<bool> IsProjectFileSupportedAsync(string projectFilePath, CancellationToken cancellationToken)
    {
        EnsureMSBuildLoaded(projectFilePath);

        return Task.FromResult(_projectFileLoaderRegistry.TryGetLoaderFromProjectPath(projectFilePath, DiagnosticReportingMode.Ignore, out var _));
    }

    public async Task<IRemoteProjectFile> LoadProjectFileAsync(string projectFilePath, CancellationToken cancellationToken)
    {
        EnsureMSBuildLoaded(projectFilePath);

        Contract.ThrowIfFalse(_projectFileLoaderRegistry.TryGetLoaderFromProjectPath(projectFilePath, out var projectLoader));
        return new RemoteProjectFile(await projectLoader.LoadProjectFileAsync(projectFilePath, _buildManager, cancellationToken).ConfigureAwait(false));
    }

    public Task ShutdownAsync()
    {
        _buildManager?.EndBatchBuild();
        _jsonRpc.Dispose();

        return Task.CompletedTask;
    }
}
