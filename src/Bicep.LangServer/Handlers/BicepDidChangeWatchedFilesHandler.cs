// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bicep.Core;
using Bicep.LanguageServer.CompilationManager;
using Bicep.LanguageServer.Configuration;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using FileSystemWatcher = OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher;

namespace Bicep.LanguageServer.Handlers
{
    public sealed class BicepDidChangeWatchedFilesHandler : DidChangeWatchedFilesHandlerBase
    {
        private readonly ICompilationManager compilationManager;
        private readonly IBicepConfigChangeHandler bicepConfigChangeHandler;

        public BicepDidChangeWatchedFilesHandler(ICompilationManager compilationManager, IBicepConfigChangeHandler bicepConfigChangeHandler)
        {
            this.bicepConfigChangeHandler = bicepConfigChangeHandler;
            this.compilationManager = compilationManager;
        }

        public override Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
        {
            Container<FileEvent> fileEvents = request.Changes;
            IEnumerable<FileEvent> bicepConfigFileChangeEvents = fileEvents.Where(x => string.Equals(Path.GetFileName(x.Uri.Path),
                                                                                       LanguageConstants.BicepConfigurationFileName,
                                                                                       StringComparison.OrdinalIgnoreCase));

            // Refresh compilation of source files in workspace when local bicepconfig.json file is created, deleted or changed
            if (bicepConfigFileChangeEvents.Any())
            {
                Uri uri = bicepConfigFileChangeEvents.First().Uri.ToUriEncoded();
                bicepConfigChangeHandler.RefreshCompilationOfSourceFilesInWorkspace();
            }

            compilationManager.HandleFileChanges(fileEvents);

            return Unit.Task;
        }

        protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities) => new()
        {
            // These file watcher globs should be kept in-sync with those defined in client.ts
            Watchers = new Container<FileSystemWatcher>(
                    new FileSystemWatcher()
                    {
                        Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete,
                        GlobPattern = "**/"
                    },
                    new FileSystemWatcher()
                    {
                        Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete,
                        GlobPattern = "**/*.bicep"
                    },
                    new FileSystemWatcher()
                    {
                        Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete,
                        GlobPattern = "**/*.{json,jsonc,arm}"
                    }
                )
        };
    }
}
