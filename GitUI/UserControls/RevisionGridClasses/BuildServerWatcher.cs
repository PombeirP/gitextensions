﻿using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitCommands;
using TeamCitySharp;
using TeamCitySharp.Locators;

namespace GitUI.RevisionGridClasses
{
    public class BuildServerWatcher : IDisposable
    {
        private readonly RevisionGrid revisionGrid;
        private readonly DvcsGraph revisions;

        private IDisposable buildStatusCancellationToken;

        public BuildServerWatcher(RevisionGrid revisionGrid, DvcsGraph revisions)
        {
            this.revisionGrid = revisionGrid;
            this.revisions = revisions;

            AddBuildStatusColumns();
        }

        private void AddBuildStatusColumns()
        {
            var buildStatusImageColumn = new DataGridViewImageColumn
            {
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                Width = 16,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var buildMessageTextBoxColumn = new DataGridViewTextBoxColumn
            {
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                HeaderText = "Build Status",
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };

            revisions.Columns.Add(buildStatusImageColumn);
            revisions.Columns.Add(buildMessageTextBoxColumn);
        }

        public void LaunchBuildServerInfoFetchOperation()
        {
            var client = new TeamCityClient("teamcity.codebetter.com");
            client.Connect(string.Empty, string.Empty, true);

            var projectName = revisionGrid.Module.GitWorkingDir.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Last();
            var initialObservable = CreateTeamCityObservable(client, projectName);
            var followingObservable = CreateTeamCityObservable(client, projectName, DateTime.Now);

            buildStatusCancellationToken =
                initialObservable.SubscribeOn(NewThreadScheduler.Default)
                    .OnErrorResumeNext(Observable.Empty<BuildInfo>())
                    .Concat(Observable.Empty<BuildInfo>()
                                .Delay(TimeSpan.FromSeconds(5))
                                .Concat(followingObservable.SubscribeOn(NewThreadScheduler.Default)
                                            .OnErrorResumeNext(Observable.Empty<BuildInfo>()))
                                .Repeat())
                    .Subscribe(item =>
                                   {
                                       if (buildStatusCancellationToken == null)
                                           return;

                                       string graphRevision;
                                       int row = revisionGrid.SearchRevision(item.CommitHash, out graphRevision);
                                       if (row >= 0)
                                       {
                                           var rowData = revisions.GetRowData(row);
                                           if (rowData.BuildStatus == null ||
                                               item.StartDate > rowData.BuildStatus.StartDate ||
                                               item.Description.Length > rowData.BuildStatus.Description.Length)
                                           {
                                               rowData.BuildStatus = item;

                                               revisions.UpdateCellValue(4, row);
                                               revisions.UpdateCellValue(5, row);
                                           }
                                       }
                                   });
        }

        private IObservable<BuildInfo> CreateTeamCityObservable(TeamCityClient client, string projectName, DateTime? sinceDate = null)
        {
            return Observable.Create<BuildInfo>((observer, cancellationToken) =>
            {
                try
                {
                    var project = client.ProjectByName(projectName);
                    var buildTypes = project.BuildTypes.BuildType;
                    var builds =
                        buildTypes.SelectMany(
                            x =>
                            client.BuildsByBuildLocator(
                                BuildLocator.WithDimensions(BuildTypeLocator.WithId(x.Id), sinceDate: sinceDate)));
                    foreach (var build in builds)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        dynamic buildExpando = client.CallByUrl<object>(build.Href.Replace("guestAuth/", string.Empty));
                        BuildInfo.BuildStatus status = BuildInfo.BuildStatus.Unknown;
                        string statusText = buildExpando.statusText;
                        string revisionVersion = buildExpando.revisions != null
                                                     ? buildExpando.revisions.revision[0].version
                                                     : null;

                        switch ((string)buildExpando.status)
                        {
                            case "SUCCESS":
                                status = BuildInfo.BuildStatus.Success;
                                break;
                            case "FAILURE":
                                status = BuildInfo.BuildStatus.Failure;
                                break;
                        }

                        var buildInfo = new BuildInfo
                                            {
                                                Id = buildExpando.id,
                                                StartDate = buildExpando.startDate,
                                                Status = status,
                                                Description = statusText,
                                                CommitHash = revisionVersion
                                            };

                        observer.OnNext(buildInfo);
                    }

                    observer.OnCompleted();
                }
                catch (Exception ex)
                {
                    observer.OnError(ex);
                }

                return new Task<IDisposable>(() => Disposable.Empty);
            });
        }

        public void CancelBuildStatusFetchOperation()
        {
            var cancellationToken = Interlocked.Exchange(ref buildStatusCancellationToken, null);

            if (cancellationToken != null)
            {
                cancellationToken.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelBuildStatusFetchOperation();
            }
        }
    }
}