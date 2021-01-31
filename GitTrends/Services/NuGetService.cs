﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using AsyncAwaitBestPractices;
using GitTrends.Shared;
using Newtonsoft.Json;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Xamarin.Essentials.Interfaces;

namespace GitTrends
{
    public class NuGetService
    {
        static readonly HttpClient _client = new();
        readonly SourceRepository _sourceRepository = NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");

        readonly IPreferences _preferences;
        readonly IAnalyticsService _analyticsService;
        readonly GitHubApiV3Service _gitHubApiV3Service;
        readonly ImageCachingService _imageCachingService;
        readonly AzureFunctionsApiService _azureFunctionsApiService;

        public NuGetService(IPreferences preferences,
                            IAnalyticsService analyticsService,
                            GitHubApiV3Service gitHubApiV3Service,
                            ImageCachingService imageCachingService,
                            AzureFunctionsApiService azureFunctionsApiService)
        {
            _preferences = preferences;
            _analyticsService = analyticsService;
            _gitHubApiV3Service = gitHubApiV3Service;
            _imageCachingService = imageCachingService;
            _azureFunctionsApiService = azureFunctionsApiService;
        }

        public IReadOnlyList<NuGetPackageModel> InstalledNugetPackages
        {
            get
            {
                var serializedInstalledNuGetPackages = _preferences.Get(nameof(InstalledNugetPackages), null);

                return serializedInstalledNuGetPackages is null
                    ? Array.Empty<NuGetPackageModel>()
                    : JsonConvert.DeserializeObject<IReadOnlyList<NuGetPackageModel>>(serializedInstalledNuGetPackages);
            }
            private set
            {
                var serializedInstalledNuGetPackages = JsonConvert.SerializeObject(value);
                _preferences.Set(nameof(InstalledNugetPackages), serializedInstalledNuGetPackages);
            }
        }

        public async ValueTask Initialize(CancellationToken cancellationToken)
        {
            if (InstalledNugetPackages.Any())
                initialize().SafeFireAndForget(ex => _analyticsService.Report(ex));
            else
                await initialize().ConfigureAwait(false);

            async Task initialize()
            {
                var installedPackagesDictionary = new Dictionary<string, (Uri IconUri, Uri NugetUri)>();

                await foreach (var packageInfo in GetPackageInfo(cancellationToken).ConfigureAwait(false))
                {
                    if (!installedPackagesDictionary.ContainsKey(packageInfo.Title))
                        installedPackagesDictionary.Add(packageInfo.Title, (packageInfo.ImageUri, packageInfo.NugetUri));
                }

                var nugetPackageModelList = new List<NuGetPackageModel>();
                foreach (var entry in installedPackagesDictionary)
                {
                    nugetPackageModelList.Add(new NuGetPackageModel(entry.Key, entry.Value.IconUri, entry.Value.NugetUri));
                }

                InstalledNugetPackages = nugetPackageModelList.OrderBy(x => x.PackageName).ToList();

                foreach (var nugetPackageModel in InstalledNugetPackages)
                    _imageCachingService.PreloadImage(nugetPackageModel.IconUri).SafeFireAndForget(ex => _analyticsService.Report(ex));
            }
        }

        public async IAsyncEnumerable<(string Title, Uri ImageUri, Uri NugetUri)> GetPackageInfo([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var packageNames = new List<string>();

            await foreach (var csprojFile in GetCsprojFiles(cancellationToken).ConfigureAwait(false))
            {
                packageNames.AddRange(GetNuGetPackageNames(csprojFile));
            }

            var metadataResource = await _sourceRepository.GetResourceAsync<PackageMetadataResource>().ConfigureAwait(false);

            var getInstalledPackageInfoTCSList = new List<(string PackageName, TaskCompletionSource<(string title, Uri iconUri, Uri nugetUri)?> PackageInfoTCS)>();

            foreach (var name in packageNames)
            {
                getInstalledPackageInfoTCSList.Add((name, new TaskCompletionSource<(string name, Uri iconUri, Uri nugetUri)?>()));
            }

            Parallel.ForEach(getInstalledPackageInfoTCSList, async package =>
            {
                const string defaultNuGetIcon = "https://www.nuget.org/Content/gallery/img/logo-og-600x600.png";
                IEnumerable<IPackageSearchMetadata> metadatas = Enumerable.Empty<IPackageSearchMetadata>();

                try
                {
                    metadatas = await metadataResource.GetMetadataAsync(package.PackageName, true, true, new SourceCacheContext(), NullLogger.Instance, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _analyticsService.Report(e, nameof(package.PackageName), package.PackageName);
                }

                var iconUri = metadatas.LastOrDefault().IconUrl;
                var nugetUri = metadatas.LastOrDefault().PackageDetailsUrl;

                if (nugetUri is null)
                    package.PackageInfoTCS.SetResult(null);
                else
                    package.PackageInfoTCS.SetResult((package.PackageName, iconUri ?? new Uri(defaultNuGetIcon), nugetUri));
            });

            var remainingTasks = getInstalledPackageInfoTCSList.Select(x => x.PackageInfoTCS.Task).ToList();

            while (remainingTasks.Any())
            {
                var completedTask = await Task.WhenAny(remainingTasks).ConfigureAwait(false);
                remainingTasks.Remove(completedTask);

                var packageInfo = await completedTask.ConfigureAwait(false);
                if (packageInfo.HasValue)
                    yield return packageInfo.Value;
            }
        }

        IReadOnlyList<string> GetNuGetPackageNames(string csProjSourceCode)
        {
            var doc = XDocument.Parse(csProjSourceCode);
            var nugetPackageNames = doc.XPathSelectElements("//PackageReference").Select(pr => pr.Attribute("Include").Value);

            return nugetPackageNames.ToList();
        }

        async IAsyncEnumerable<string> GetCsprojFiles([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var csprojFilePaths = await _azureFunctionsApiService.GetGitTrendsCSProjPaths(cancellationToken).ConfigureAwait(false);

            var getCSProjFileTaskList = new List<(string csprojFilePath, TaskCompletionSource<string> csprojSourceCodeTCS)>();
            foreach (var csprojFilePath in csprojFilePaths)
            {
                getCSProjFileTaskList.Add((csprojFilePath, new TaskCompletionSource<string>()));
            }

            Parallel.ForEach(getCSProjFileTaskList, async getCSProjFileTask =>
            {
                var repositoryFile = await _gitHubApiV3Service.GetGitTrendsFile(getCSProjFileTask.csprojFilePath, cancellationToken).ConfigureAwait(false);
                var file = await _client.GetStringAsync(repositoryFile.DownloadUrl).ConfigureAwait(false);

                getCSProjFileTask.csprojSourceCodeTCS.SetResult(file);
            });

            var remainingTasks = getCSProjFileTaskList.Select(x => x.csprojSourceCodeTCS.Task).ToList();

            while (remainingTasks.Any())
            {
                var completedTask = await Task.WhenAny(remainingTasks).ConfigureAwait(false);
                remainingTasks.Remove(completedTask);

                var csprojFile = await completedTask.ConfigureAwait(false);
                yield return csprojFile;
            }
        }
    }
}
