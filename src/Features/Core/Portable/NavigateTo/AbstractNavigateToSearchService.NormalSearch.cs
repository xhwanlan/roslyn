﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.PatternMatching;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Storage;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.NavigateTo
{
    internal abstract partial class AbstractNavigateToSearchService
    {
        public async Task SearchDocumentAsync(
            Document document,
            string searchPattern,
            IImmutableSet<string> kinds,
            // Document? activeDocument,
            Func<Project, INavigateToSearchResult, Task> onResultFound,
            CancellationToken cancellationToken)
        {
            var solution = document.Project.Solution;
            var onItemFound = GetOnItemFoundCallback(solution, activeDocument: null, onResultFound, cancellationToken);

            var client = await RemoteHostClient.TryGetClientAsync(document.Project, cancellationToken).ConfigureAwait(false);
            if (client != null)
            {
                var callback = new NavigateToSearchServiceCallback(onItemFound, onProjectCompleted: null);
                // Don't need to sync the full solution when searching a single document.  Just sync the project that doc is in.
                await client.TryInvokeAsync<IRemoteNavigateToSearchService>(
                    document.Project,
                    (service, solutionInfo, callbackId, cancellationToken) =>
                    service.SearchDocumentAsync(solutionInfo, document.Id, searchPattern, kinds.ToImmutableArray(), callbackId, cancellationToken),
                    callback, cancellationToken).ConfigureAwait(false);

                return;
            }

            await SearchDocumentInCurrentProcessAsync(document, searchPattern, kinds, onItemFound, cancellationToken).ConfigureAwait(false);
        }

        public static Task SearchDocumentInCurrentProcessAsync(Document document, string searchPattern, IImmutableSet<string> kinds, Func<RoslynNavigateToItem, Task> onItemFound, CancellationToken cancellationToken)
        {
            return SearchProjectInCurrentProcessAsync(
                document.Project, priorityDocuments: ImmutableArray<Document>.Empty, document, searchPattern, kinds, onItemFound, cancellationToken);
        }

        public async Task SearchProjectAsync(
            Project project,
            ImmutableArray<Document> priorityDocuments,
            string searchPattern,
            IImmutableSet<string> kinds,
            Document? activeDocument,
            Func<INavigateToSearchResult, Task> onResultFound,
            CancellationToken cancellationToken)
        {
            var solution = project.Solution;
            var onItemFound = GetOnItemFoundCallback(solution, activeDocument, onResultFound, cancellationToken);

            var client = await RemoteHostClient.TryGetClientAsync(project, cancellationToken).ConfigureAwait(false);
            if (client != null)
            {
                var priorityDocumentIds = priorityDocuments.SelectAsArray(d => d.Id);
                var callback = new NavigateToSearchServiceCallback(onItemFound);

                await client.TryInvokeAsync<IRemoteNavigateToSearchService>(
                    // Intentionally sync the full solution.   When SearchProjectAsync is called, we're searching all
                    // projects (just in parallel).  So best for them all to sync and share a single solution snapshot
                    // on the oop side.
                    solution,
                    (service, solutionInfo, callbackId, cancellationToken) =>
                        service.SearchProjectAsync(solutionInfo, project.Id, priorityDocumentIds, searchPattern, kinds.ToImmutableArray(), callbackId, cancellationToken),
                    callback, cancellationToken).ConfigureAwait(false);

                return;
            }

            await SearchProjectInCurrentProcessAsync(project, priorityDocuments, searchPattern, kinds, onItemFound, cancellationToken).ConfigureAwait(false);
        }

        public static Task SearchProjectInCurrentProcessAsync(Project project, ImmutableArray<Document> priorityDocuments, string searchPattern, IImmutableSet<string> kinds, Func<RoslynNavigateToItem, Task> onItemFound, CancellationToken cancellationToken)
        {
            return SearchProjectInCurrentProcessAsync(
                project, priorityDocuments, searchDocument: null,
                pattern: searchPattern, kinds, onItemFound, cancellationToken);
        }
    }
}
