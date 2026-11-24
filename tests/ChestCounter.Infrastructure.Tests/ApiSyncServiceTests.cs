using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChestCounter;
using Xunit;

namespace ChestCounter.Infrastructure.Tests;

public sealed class ApiSyncServiceTests
{
    private static readonly string TestJwt = MakeJwt(DateTimeOffset.UtcNow.AddHours(1), "a");

    [Fact]
    public void ApiConfigurationHoldsOnlyDistributableSettings()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "api-config.json");
        var store = new ApiIntegrationConfigStore(path);

        var config = store.Load();

        Assert.True(config.Enabled);
        Assert.Equal(ApiIntegrationConfigStore.DefaultBaseUrl, config.BaseUrl);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyBearerTokenIsRemovedFromTheConfigurationFile()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "api-config.json");
        File.WriteAllText(path, """{"enabled":true,"baseUrl":"https://example.test/api/v1/","bearerToken":"legacy-secret"}""");
        var store = new ApiIntegrationConfigStore(path);

        var config = store.Load();

        Assert.Equal("https://example.test/api/v1/", config.BaseUrl);
        Assert.DoesNotContain("legacy-secret", File.ReadAllText(path));
        Assert.DoesNotContain("bearerToken", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidApiUrlDoesNotReplacePreviousConfiguration()
    {
        using var folder = new TemporaryFolder();
        var store = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var original = store.Load().BaseUrl;

        Assert.Throws<ArgumentException>(() => store.UpdateBaseUrl("ftp://invalid.example"));

        Assert.Equal(original, store.Load().BaseUrl);
    }

    [Fact]
    public void CannotCreateDefaultApiConfigurationIsReportedWithoutThrowing()
    {
        using var folder = new TemporaryFolder();
        var blockingFile = Path.Combine(folder.Path, "not-a-directory");
        File.WriteAllText(blockingFile, "block");
        var store = new ApiIntegrationConfigStore(Path.Combine(blockingFile, "api-config.json"));

        _ = store.Load();

        Assert.False(string.IsNullOrEmpty(store.LoadError));
    }

    [Fact]
    public async Task RemotePlayerResolutionUsesAuthenticatedEpicIdLookup()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        HttpRequestMessage? lookupRequest = null;
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))
        {
            ProtectedResponderAsync = (request, _) =>
            {
                lookupRequest = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PlayerPage((170, "0123456789ABCDEF0123456789ABCDEF", "Player"))),
                });
            },
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync("0123456789abcdef0123456789abcdef", "OtherName");

        Assert.Equal(FortCardsPlayerResolutionStatus.Found, result.Status);
        Assert.Equal(170, result.PlayerId);
        Assert.Contains("epic_id=0123456789abcdef0123456789abcdef", lookupRequest!.RequestUri!.Query);
        Assert.DoesNotContain("name=", lookupRequest.RequestUri.Query);
        Assert.Equal("Bearer " + TestJwt, lookupRequest.Headers.Authorization!.ToString());
        Assert.Equal(1, handler.AuthCalls);
    }

    [Fact]
    public async Task RemotePlayerResolutionCompletesWhileLocalReplayProcessingIsActive()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayerPage((170, "epic-id", "Player"))),
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        service.SetLocalProcessing(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await service.ResolveFortCardsPlayerAsync("epic-id", null, timeout.Token);

        Assert.Equal(FortCardsPlayerResolutionStatus.Found, result.Status);
        Assert.Equal(170, result.PlayerId);
        Assert.Equal(1, handler.AuthCalls);
        Assert.Equal(1, handler.ProtectedCalls);
    }

    [Fact]
    public async Task ProfileResolutionDoesNotResumePausedReplayUploads()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var handler = new StubHttpMessageHandler((request, _) =>
            request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PlayerPage((170, "epic-id", "Player"))),
                }
                : new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));
        service.SetLocalProcessing(true);
        var syncTask = service.SyncNowAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await service.ResolveFortCardsPlayerAsync("epic-id", null, timeout.Token);

        Assert.Equal(170, result.PlayerId);
        Assert.Equal(1, handler.AuthCalls);
        Assert.Equal(1, handler.ProtectedCalls);
        Assert.False(syncTask.IsCompleted);

        service.SetLocalProcessing(false);
        await syncTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.AuthCalls);
        Assert.Equal(2, handler.ProtectedCalls);
    }

    [Fact]
    public async Task ReportCommandOpensTheResolvedProfileInTheExternalBrowser()
    {
        using var folder = new TemporaryFolder();
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("The saved identity should be reused.")));
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);
        string? openedUrl = null;
        using var dashboard = CreateDashboard(folder.Path, service, url => openedUrl = url, out _);

        await dashboard.OpenReportCommand.ExecuteAsync(null);

        Assert.Equal("https://contadordebau.fortcards.me/jogadores/170", openedUrl);
    }

    [Fact]
    public async Task ReportCommandReportsBrowserLaunchFailure()
    {
        using var folder = new TemporaryFolder();
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("The saved identity should be reused.")));
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);
        using var dashboard = CreateDashboard(folder.Path, service,
            _ => throw new System.ComponentModel.Win32Exception("Browser unavailable"), out var monitor);
        var oldLogPath = ErrorLog.FilePath;
        var logPath = Path.Combine(folder.Path, "errors.log");
        try
        {
            ErrorLog.FilePath = logPath;

            await dashboard.OpenReportCommand.ExecuteAsync(null);

            var notice = typeof(ChestCounter.Monitor)
                .GetField("_notice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(monitor);
            Assert.Equal(L.T("main.remote_profile_open_failed"), notice);
            Assert.Contains("Browser unavailable", File.ReadAllText(logPath));
        }
        finally
        {
            ErrorLog.FilePath = oldLogPath;
        }
    }

    [Fact]
    public async Task ReportCommandOpensPlayersPageWithoutPlayerIdentity()
    {
        using var folder = new TemporaryFolder();
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("No lookup should be made without an identity.")));
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);
        string? openedUrl = null;
        using var dashboard = CreateDashboard(folder.Path, service, url => openedUrl = url, out _, hasIdentity: false);

        await dashboard.OpenReportCommand.ExecuteAsync(null);

        Assert.Equal(RemoteProfileUrl.PlayersUrl, openedUrl);
    }

    [Fact]
    public async Task RemotePlayerResolutionUsesExactNameOnlyWhenEpicIdIsUnavailable()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        HttpRequestMessage? lookupRequest = null;
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))
        {
            ProtectedResponderAsync = (request, _) =>
            {
                lookupRequest = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PlayerPage(
                        (170, "epic-player", "Exact Name & 2"),
                        (171, "other-player", "ExactNameSimilar"))),
                });
            },
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync(null, "Exact Name & 2");

        Assert.Equal(FortCardsPlayerResolutionStatus.Found, result.Status);
        Assert.Equal(170, result.PlayerId);
        Assert.Contains("name=Exact%20Name%20%26%202", lookupRequest!.RequestUri!.Query);
        Assert.DoesNotContain("epic_id=", lookupRequest.RequestUri.Query);
    }

    [Fact]
    public async Task RemotePlayerResolutionFallsBackToNameWhenEpicIdIsNotFound()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var handler = new StubHttpMessageHandler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Query.Contains("epic_id=")
                ? PlayerPage()
                : PlayerPage((170, null, "c3bola"))),
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync("missing-epic-id", "c3bola");

        Assert.Equal(FortCardsPlayerResolutionStatus.Found, result.Status);
        Assert.Equal(170, result.PlayerId);
        Assert.Equal(2, handler.ProtectedCalls);
    }

    [Fact]
    public async Task NameLookupRejectsAConflictingEpicId()
    {
        using var folder = new TemporaryFolder();
        var handler = new StubHttpMessageHandler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Query.Contains("epic_id=")
                ? PlayerPage() : PlayerPage((170, "other-epic-id", "c3bola"))),
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync("epic-id", "c3bola");

        Assert.Equal(FortCardsPlayerResolutionStatus.Ambiguous, result.Status);
    }

    [Fact]
    public async Task RemotePlayerResolutionRejectsAmbiguousNamesAndInvalidResponses()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var responseBody = PlayerPage(
            (170, "first-epic-id", "SameName"),
            (171, "second-epic-id", "SameName"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody),
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var ambiguous = await service.ResolveFortCardsPlayerAsync(null, "SameName");
        Assert.Equal(FortCardsPlayerResolutionStatus.Ambiguous, ambiguous.Status);

        handler.ProtectedResponderAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success":true,"data":[]}"""),
        });
        var invalid = await service.ResolveFortCardsPlayerAsync(null, "SameName");
        Assert.Equal(FortCardsPlayerResolutionStatus.Unavailable, invalid.Status);
    }

    [Fact]
    public async Task RemotePlayerResolutionTreatsApiFailureAsUnavailable()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync("epic-id", null);

        Assert.Equal(FortCardsPlayerResolutionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ReportCommandResolvesC3bolaByNameAndOpensGlobalId()
    {
        using var folder = new TemporaryFolder();
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.Query.Contains("epic_id=")
                    ? PlayerPage() : PlayerPage((170, null, "c3bola"))),
            };
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);
        string? openedUrl = null;
        using var dashboard = CreateDashboard(folder.Path, service, url => openedUrl = url, out var monitor,
            hasCachedIdentity: false, playerName: "c3bola");

        await dashboard.OpenReportCommand.ExecuteAsync(null);

        Assert.Equal("https://contadordebau.fortcards.me/jogadores/170", openedUrl);
        Assert.Equal(170, monitor.GetFortCardsPlayerIdentity("epic:EPIC-ID")!.PlayerId);
        Assert.Contains(requests, query => query.Contains("epic_id=epic-id"));
        Assert.Contains(requests, query => query.Contains("name=c3bola"));
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("unavailable")]
    [InlineData("timeout")]
    [InlineData("ambiguous")]
    public async Task ReportCommandOpensPlayersPageWhenIdentityCannotBeResolved(string scenario)
    {
        using var folder = new TemporaryFolder();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (scenario == "unavailable")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/analytics/screens/search", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchPage()) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(scenario == "ambiguous"
                    ? PlayerPage((1, null, "c3bola"), (2, null, "c3bola"))
                    : PlayerPage()),
            };
        });
        if (scenario == "timeout")
        {
            handler.ProtectedResponderAsync = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("The timeout should cancel the request.");
            };
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);
        string? openedUrl = null;
        using var dashboard = CreateDashboard(folder.Path, service, url => openedUrl = url, out _,
            hasCachedIdentity: false, playerName: "c3bola", epicId: null);

        await dashboard.OpenReportCommand.ExecuteAsync(null);

        Assert.Equal(RemoteProfileUrl.PlayersUrl, openedUrl);
    }

    [Fact]
    public async Task PublicSearchUsesUniqueExactNameWithoutAuthentication()
    {
        using var folder = new TemporaryFolder();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/analytics/screens/search", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SearchPage((170, "c3bola"), (171, "c3bola-extra"))),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync(null, "c3bola");

        Assert.Equal(FortCardsPlayerResolutionStatus.Found, result.Status);
        Assert.Equal(170, result.PlayerId);
    }

    [Fact]
    public async Task PublicSearchDoesNotUseConflictingEpicIdentity()
    {
        using var folder = new TemporaryFolder();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/analytics/screens/search", StringComparison.Ordinal)
                ? SearchPage((170, "c3bola"))
                : path.EndsWith("/analytics/players/170/summary", StringComparison.Ordinal)
                    ? """{"success":true,"data":{"player_id":170,"epic_id":"different-epic"}}"""
                    : PlayerPage();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync("epic-id", "c3bola");

        Assert.Equal(FortCardsPlayerResolutionStatus.Ambiguous, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicSearchRejectsAmbiguousOrIncompleteResults(bool incomplete)
    {
        using var folder = new TemporaryFolder();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var body = incomplete
                ? """{"success":true,"data":{"items":[{"player_id":170,"display_name":"c3bola"}],"total":101,"page":1,"page_size":100}}"""
                : SearchPage((170, "c3bola"), (171, "c3bola"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/analytics/screens/search", StringComparison.Ordinal)
                    ? body : PlayerPage()),
            };
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync(null, "c3bola");

        Assert.Equal(FortCardsPlayerResolutionStatus.Ambiguous, result.Status);
    }

    [Fact]
    public async Task RemotePlayerResolutionTreatsTimeoutAsUnavailable()
    {
        using var folder = new TemporaryFolder();
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException())
        {
            ProtectedResponderAsync = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("The timeout should cancel the request.");
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.ResolveFortCardsPlayerAsync(null, "c3bola");

        Assert.Equal(FortCardsPlayerResolutionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task TemporaryApiFailureDoesNotEraseCachedIdentity()
    {
        using var folder = new TemporaryFolder();
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);
        string? openedUrl = null;
        using var dashboard = CreateDashboard(folder.Path, service, url => openedUrl = url, out var monitor,
            cachedAt: DateTimeOffset.UtcNow.AddDays(-2));

        await dashboard.OpenReportCommand.ExecuteAsync(null);
        var task = (Task?)typeof(DashboardViewModel)
            .GetField("_remoteProfileResolutionTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(dashboard);
        if (task != null)
        {
            await task;
        }

        Assert.Equal("https://contadordebau.fortcards.me/jogadores/170", openedUrl);
        Assert.Equal(170, monitor.GetFortCardsPlayerIdentity("epic:EPIC-ID")!.PlayerId);
    }

    [Fact]
    public async Task CachedIdentityIsUpdatedWhenLookupFindsANewGlobalId()
    {
        using var folder = new TemporaryFolder();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler((_, _) => throw new InvalidOperationException())
        {
            ProtectedResponderAsync = (_, _) => response.Task,
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json")),
            Path.Combine(folder.Path, "api-sync-queue.json"), client);
        string? openedUrl = null;
        using var dashboard = CreateDashboard(folder.Path, service, url => openedUrl = url, out var monitor,
            cachedAt: DateTimeOffset.UtcNow.AddDays(-2));

        await dashboard.OpenReportCommand.ExecuteAsync(null);
        var refresh = (Task)typeof(DashboardViewModel)
            .GetField("_remoteProfileResolutionTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(dashboard)!;
        Assert.Equal("https://contadordebau.fortcards.me/jogadores/170", openedUrl);
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(PlayerPage((171, "epic-id", "Player"))),
        });
        await refresh;

        Assert.Equal(171, monitor.GetFortCardsPlayerIdentity("epic:EPIC-ID")!.PlayerId);
    }

    [Fact]
    public void FortCardsIdentityPersistencePreservesLegacySettingsAndDropsManualProfileFields()
    {
        using var folder = new TemporaryFolder();
        var store = new Store(folder.Path);
        File.WriteAllText(store.ConfigPath, """
        {
          "RemoteProfileId": "170",
          "RemoteProfileSkinId": "legacy-skin",
          "StartWithFortnite": true,
          "OpenReportAtStartup": true,
          "DemosFolder": "replays"
        }
        """);

        var config = store.LoadConfig()!;
        Assert.True(config.StartWithFortnite);
        Assert.True(config.OpenReportOnStart);
        Assert.Equal("replays", config.DemosFolder);
        Assert.Empty(config.FortCardsPlayerIdentities);

        config.FortCardsPlayerIdentities["epic:0123456789ABCDEF0123456789ABCDEF"] =
            new FortCardsPlayerIdentity(170, DateTimeOffset.UtcNow);
        store.SaveConfig(config);
        var persisted = store.LoadConfig()!;
        Assert.Equal(170, persisted.FortCardsPlayerIdentities["epic:0123456789ABCDEF0123456789ABCDEF"].PlayerId);
        var savedJson = File.ReadAllText(store.ConfigPath);
        Assert.DoesNotContain("RemoteProfileId", savedJson);
        Assert.DoesNotContain("RemoteProfileSkinId", savedJson);
    }

    [Fact]
    public void ConfirmedMigrationClearsLegacyRawDataAndCreatesBackup()
    {
        using var folder = new TemporaryFolder();
        var store = new Store(folder.Path);
        var hash = new string('a', 64);
        var data = new TrackerData
        {
            Matches =
            [
                new MatchRecord
                {
                    SessionId = "legacy",
                    ReplayFileSha256 = hash,
                    RawReplayData = CreateRawSnapshot(),
                },
            ],
        };
        store.SaveData(data);

        var migrated = store.MigrateConfirmedRawReplayData(data, [hash]);

        Assert.Equal(1, migrated);
        Assert.Null(Assert.Single(store.LoadData().Matches).RawReplayData);
        Assert.True(Assert.Single(data.Matches).IsSynced);
        Assert.True(Directory.EnumerateFiles(folder.Path, "data.json.pre-raw-migration-*").Any());
    }

    [Fact]
    public void FailedReplayRegistryPersistsWithoutReplayBytes()
    {
        using var folder = new TemporaryFolder();
        var store = new Store(folder.Path);
        var failure = new FailedReplay
        {
            ReplayFileName = "broken.replay",
            ReplayHash = new string('b', 64),
            FilePath = Path.Combine(folder.Path, "broken.replay"),
            FileSizeBytes = 1234,
            FailureStage = "analysis",
            ErrorType = "InvalidDataException",
            ErrorMessage = "Corrupt replay",
            Attempts = 2,
            AnalyzerVersion = "15",
            ApplicationVersion = "3.3.4",
        };

        store.SaveFailedReplays([failure]);
        var loaded = store.LoadFailedReplays();

        var saved = Assert.Single(loaded);
        Assert.Equal("broken.replay", saved.ReplayFileName);
        Assert.Equal(2, saved.Attempts);
        Assert.DoesNotContain("ReplayBytes", File.ReadAllText(store.FailedReplaysPath));
    }

    [Fact]
    public void HistoricalQueueWriteFailureDoesNotInterruptLocalHistory()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        _ = configStore.Load();
        var blockingFile = Path.Combine(folder.Path, "not-a-directory");
        File.WriteAllText(blockingFile, "block");
        using var service = new ApiSyncService(configStore, Path.Combine(blockingFile, "api-sync-queue.json"));
        var data = new TrackerData
        {
            Matches =
            [
                new MatchRecord
                {
                    SessionId = "historic-session",
                    ReplayFile = "historic.replay",
                    ReplayFileSha256 = new string('a', 64),
                    RawReplayData = CreateRawSnapshot(),
                },
            ],
        };

        service.QueueHistorical(data, Array.Empty<FileInfo>());

        Assert.Contains("could not be added", service.State.Status);
    }

    [Fact]
    public void MapperUsesReplayHashForStableIdentityAndOnlyAvailableData()
    {
        using var folder = new TemporaryFolder();
        var replayPath = Path.Combine(folder.Path, "match.replay");
        File.WriteAllText(replayPath, "original replay bytes");
        var replay = new FileInfo(replayPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("original replay bytes"))).ToLowerInvariant();
        var match = new MatchRecord
        {
            SessionId = "session-123",
            EpicId = "0123456789ABCDEF0123456789ABCDEF",
            PlayerName = "Player",
            IsReplayOwner = true,
            Placement = 2,
            Eliminations = 4,
            DurationSeconds = 600,
            Chests =
            [
                new ChestRecord { TimeSeconds = 12.5f, Kind = "Chest", Blueprint = "B_Chest", Destroyed = true },
            ],
            TreasureFinds =
            [
                new TreasureFind { PrizeId = "Prize:Known", ObtainedAtSeconds = 20, FromGoldenChest = true },
                new TreasureFind { Item = "unknown-without-prize-id" },
            ],
            RawReplayData = CreateRawSnapshot(),
        };

        var mapped = ReplayIngestionMapper.Create(match, replay, hash, "3.2.0", "1.0.0");
        var repeated = ReplayIngestionMapper.Create(match, replay, hash, "3.2.0", "1.0.0");

        Assert.Equal(hash, mapped.ReplayFile.FileSha256);
        Assert.Equal(mapped.Request.IdempotencyKey, repeated.Request.IdempotencyKey);
        Assert.Equal("chestcounter", mapped.Request.ApplicationId);
        Assert.Same(match.RawReplayData, mapped.Request.Data);
    }

    // ---- Authentication service ----

    [Fact]
    public async Task AuthServiceObtainsTokenAutomaticallyWithoutSendingCredentials()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);
        var auth = CreateAuth(client, out _);

        var token = await auth.GetTokenAsync(new Uri("https://example.test/api/v1/"), default);

        Assert.Equal(TestJwt, token);
        Assert.Equal(1, handler.AuthCalls);
        Assert.Null(handler.LastAuthAuthorization);
        Assert.Equal(ApiAuthState.Authenticated, auth.Status.State);
    }

    [Fact]
    public async Task AuthServiceReusesValidTokenAndRenewsAfterExpiration()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent))
        {
            AuthResponder = _ => TokenResponse(MakeJwt(DateTimeOffset.UtcNow.AddHours(1), "x")),
        };
        using var client = new HttpClient(handler);
        var auth = CreateAuth(client, out var clock, () => now);
        var baseUri = new Uri("https://example.test/api/v1/");

        var first = await auth.GetTokenAsync(baseUri, default);
        var second = await auth.GetTokenAsync(baseUri, default);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.AuthCalls);

        now = now.AddHours(2);
        Assert.Equal(ApiAuthState.Expired, auth.Status.State);
        handler.AuthResponder = _ => TokenResponse(MakeJwt(now.AddHours(1), "y"));
        var renewed = await auth.GetTokenAsync(baseUri, default);

        Assert.NotEqual(first, renewed);
        Assert.Equal(2, handler.AuthCalls);
        Assert.Equal(ApiAuthState.Authenticated, auth.Status.State);
    }

    [Fact]
    public async Task AuthServiceRejectsUnusableAuthorizationResponses()
    {
        var cases = new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":false,\"token\":\"" + TestJwt + "\"}") },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"success\":true}") },
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ not json") },
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => throw new HttpRequestException("connection refused"),
            () => throw new TaskCanceledException("timeout"),
        };
        foreach (var respond in cases)
        {
            var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent))
            {
                AuthResponder = _ => respond(),
            };
            using var client = new HttpClient(handler);
            var auth = CreateAuth(client, out _);

            await Assert.ThrowsAsync<ApiAuthException>(() => auth.GetTokenAsync(new Uri("https://example.test/api/v1/"), default));

            Assert.Equal(ApiAuthState.Failed, auth.Status.State);
            Assert.True(auth.IsInFailureCooldown);
        }
    }

    [Fact]
    public async Task AuthServiceRejectsExpiredOrMalformedTokens()
    {
        foreach (var bad in new[] { MakeJwt(DateTimeOffset.UtcNow.AddMinutes(-5), "e"), "not-a-jwt", MakeJwtWithoutExp() })
        {
            var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent))
            {
                AuthResponder = _ => TokenResponse(bad),
            };
            using var client = new HttpClient(handler);
            var auth = CreateAuth(client, out _);

            await Assert.ThrowsAsync<ApiAuthException>(() => auth.GetTokenAsync(new Uri("https://example.test/api/v1/"), default));
            Assert.Equal(ApiAuthState.Failed, auth.Status.State);
        }
    }

    [Fact]
    public async Task AuthServiceDoesNotCallTheMockEndpointUnlessAuthorizedForProduction()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);
        var auth = new ApiAuthService((r, ct) => client.SendAsync(r, ct), mockEndpointAuthorized: false);

        var ex = await Assert.ThrowsAsync<ApiAuthException>(() => auth.GetTokenAsync(new Uri("https://example.test/api/v1/"), default));

        Assert.True(ex.NotAuthorized);
        Assert.Equal(0, handler.AuthCalls);
    }

    [Fact]
    public async Task ConcurrentCallersShareASingleTokenRequest()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent))
        {
            AuthDelay = TimeSpan.FromMilliseconds(150),
        };
        using var client = new HttpClient(handler);
        var auth = CreateAuth(client, out _);
        var baseUri = new Uri("https://example.test/api/v1/");

        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => auth.GetTokenAsync(baseUri, default)));

        Assert.All(tokens, token => Assert.Equal(TestJwt, token));
        Assert.Equal(1, handler.AuthCalls);
    }

    [Fact]
    public async Task InvalidateOnlyDiscardsTheRejectedToken()
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler);
        var auth = CreateAuth(client, out _);
        var baseUri = new Uri("https://example.test/api/v1/");
        var token = await auth.GetTokenAsync(baseUri, default);

        auth.Invalidate("some-older-token");
        Assert.Equal(ApiAuthState.Authenticated, auth.Status.State);

        auth.Invalidate(token);
        Assert.Equal(ApiAuthState.Expired, auth.Status.State);
        await auth.GetTokenAsync(baseUri, default);
        Assert.Equal(2, handler.AuthCalls);
    }

    // ---- Synchronization with automatic authentication ----

    [Fact]
    public async Task SyncObtainsTokenAutomaticallyAndSendsItOnlyToProtectedEndpoints()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        _ = configStore.Load();
        var requests = new List<CapturedRequest>();
        var handler = new StubHttpMessageHandler((request, body) =>
        {
            requests.Add(new CapturedRequest(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var client = new HttpClient(handler);
        var queuePath = Path.Combine(folder.Path, "api-sync-queue.json");
        var diagnostics = new List<ApiOperationLogEntry>();
        var progressUpdates = new List<ApiOperationProgress>();
        using (var service = CreateService(configStore, queuePath, client))
        {
            service.DiagnosticReported += diagnostics.Add;
            service.ProgressChanged += progressUpdates.Add;
            await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

            var summary = await service.SyncNowAsync();

            Assert.Equal(1, summary.Synchronized);
            Assert.Equal(0, summary.Pending);
            Assert.Equal(1, handler.AuthCalls);
            Assert.Null(handler.LastAuthAuthorization);
            Assert.Equal(ApiAuthState.Authenticated, service.AuthStatus.State);
            Assert.Single(requests);
            Assert.EndsWith("/processing-attempts", requests[0].Path);
            Assert.All(requests, request => Assert.Equal("Bearer " + TestJwt, request.Authorization));
            using var requestBody = JsonDocument.Parse(requests[0].Body);
            Assert.Equal("chestcounter", requestBody.RootElement.GetProperty("application_id").GetString());
            Assert.False(string.IsNullOrEmpty(requestBody.RootElement.GetProperty("idempotency_key").GetString()));
            Assert.Equal(JsonValueKind.Object, requestBody.RootElement.GetProperty("analyzer_version").ValueKind);
            Assert.Equal("EventsOnly", requestBody.RootElement.GetProperty("data").GetProperty("parseMode").GetString());
            Assert.Contains(progressUpdates, progress => progress.Total == 1 && progress.Processed == 1 && progress.Succeeded == 1);
        }

        var logText = File.ReadAllText(Path.Combine(folder.Path, "api-sync.log"));
        Assert.DoesNotContain(TestJwt, logText);
        Assert.DoesNotContain(TestJwt, string.Join(" ", diagnostics.Select(entry =>
            $"{entry.Description} {entry.RequestBody} {entry.ResponseSummary} {entry.ErrorDetails}")));
        Assert.DoesNotContain("token", File.ReadAllText(configStore.Path), StringComparison.OrdinalIgnoreCase);

        using var restartedClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("A completed queue item must not be sent again.")));
        using var restarted = CreateService(configStore, queuePath, restartedClient);
        Assert.Equal(0, restarted.State.Pending);
        Assert.Equal(0, restarted.State.Failed);

        using var queueDocument = JsonDocument.Parse(File.ReadAllText(queuePath));
        Assert.Equal(1, queueDocument.RootElement.GetArrayLength());
        Assert.False(queueDocument.RootElement[0].GetProperty("request").TryGetProperty("data", out var data)
                   && data.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task SyncWaitsUntilLocalProcessingFinishesBeforeSendingRequests()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        _ = configStore.Load();
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));
        service.SetLocalProcessing(true);

        var syncTask = service.SyncNowAsync();
        await Task.Delay(100);

        Assert.Equal(0, handler.AuthCalls);
        Assert.Equal(0, handler.ProtectedCalls);

        service.SetLocalProcessing(false);
        var summary = await syncTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, summary.Synchronized);
        Assert.Equal(1, handler.AuthCalls);
        Assert.Equal(1, handler.ProtectedCalls);
    }

    [Fact]
    public async Task AutomaticSyncWaitsForLocalProcessingAndResumesAfterTheQueueDrains()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        _ = configStore.Load();
        var requestSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created))
        {
            ProtectedResponderAsync = (_, _) =>
            {
                requestSent.TrySetResult(true);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
            },
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));
        service.SetLocalProcessing(true);
        service.Start();

        await Task.Delay(100);
        Assert.Equal(0, handler.AuthCalls);
        Assert.Equal(0, handler.ProtectedCalls);

        service.SetLocalProcessing(false);
        await requestSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.State.Pending > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, handler.ProtectedCalls);
        Assert.Equal(0, service.State.Pending);
    }

    [Fact]
    public async Task SyncFinishesInFlightRequestThenPausesBeforeTheNextReplay()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        _ = configStore.Load();
        var firstRequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerRequestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created))
        {
            ProtectedResponderAsync = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref handlerRequestCount) == 1)
                {
                    firstRequestStarted.TrySetResult(true);
                    await releaseFirstRequest.Task.WaitAsync(cancellationToken);
                }
                return new HttpResponseMessage(HttpStatusCode.Created);
            },
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        var firstReplay = WriteReplay(folder.Path);
        var secondReplay = WriteReplay(folder.Path);
        File.WriteAllText(firstReplay.FullName, "first replay");
        File.WriteAllText(secondReplay.FullName, "second replay");
        await service.QueueReplayAsync(CreateMatch(), firstReplay);
        await service.QueueReplayAsync(CreateMatch(), secondReplay);

        var syncTask = service.SyncNowAsync();
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.SetLocalProcessing(true);
        releaseFirstRequest.TrySetResult(true);
        await Task.Delay(100);

        Assert.Equal(1, handler.ProtectedCalls);
        Assert.False(syncTask.IsCompleted);

        service.SetLocalProcessing(false);
        var summary = await syncTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, summary.Synchronized);
        Assert.Equal(2, handler.ProtectedCalls);
    }

    [Fact]
    public async Task SyncBuffersAuthenticationResponseBeforeReadingItsDiagnosticPreview()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        _ = configStore.Load();
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created))
        {
            AuthResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new SingleUseResponseContent(JsonSerializer.Serialize(new
                {
                    success = true,
                    token = TestJwt,
                })),
            },
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(
            configStore,
            Path.Combine(folder.Path, "api-sync-queue.json"),
            client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        var summary = await service.SyncNowAsync();

        Assert.Equal(1, summary.Synchronized);
        Assert.Equal(ApiAuthState.Authenticated, service.AuthStatus.State);
        Assert.Equal(1, handler.AuthCalls);
    }

    [Fact]
    public async Task AutomaticSynchronizationAuthenticatesAndSendsBearerOnlyToProtectedEndpoints()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var authorizations = new List<string?>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            lock (authorizations)
            {
                authorizations.Add(request.Headers.Authorization?.ToString());
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        service.Start();
        for (var i = 0; i < 100 && service.State.Pending > 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, service.State.Pending);
        Assert.Equal(1, handler.AuthCalls);
        Assert.Null(handler.LastAuthAuthorization);
        lock (authorizations)
        {
            Assert.Single(authorizations);
            Assert.All(authorizations, header => Assert.Equal("Bearer " + TestJwt, header));
        }
    }

    [Fact]
    public async Task AuthenticationStepsAreReportedAndFailureIsNotShownAsCompleted()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var diagnostics = new List<ApiOperationLogEntry>();
        ApiOperationProgress? last = null;
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created))
        {
            AuthResponder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        service.DiagnosticReported += diagnostics.Add;
        service.ProgressChanged += progress => last = progress;
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        var summary = await service.SyncNowAsync();

        Assert.Contains(diagnostics, entry => entry.Description == "Requesting authentication token.");
        Assert.Contains(diagnostics, entry => entry.Endpoint == "auth/mock" && entry.HttpStatusCode == 502);
        var authFailure = Assert.Single(diagnostics, entry => entry.Description == "Authentication failed.");
        Assert.Contains("RequestTokenAsync", authFailure.ErrorDetails);
        Assert.DoesNotContain(diagnostics, entry => entry.Description.Contains("Replay synchronized"));
        Assert.Equal("Operation failed", last?.Stage);
        Assert.DoesNotContain("completed successfully", summary.Message);
        Assert.Contains("not started", last?.Status);
    }

    [Fact]
    public async Task ValidTokenIsReusedAcrossOperations()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        await service.SyncNowAsync();
        await service.TestConnectionAsync();

        Assert.Equal(1, handler.AuthCalls);
    }

    [Fact]
    public async Task Http401RenewsTheTokenAndSynchronizationRecovers()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var secondJwt = MakeJwt(DateTimeOffset.UtcNow.AddHours(1), "second");
        var issued = 0;
        var seen = new List<string?>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var header = request.Headers.Authorization?.Parameter;
            seen.Add(header);
            return header == TestJwt
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.Created);
        })
        {
            AuthResponder = _ => TokenResponse(Interlocked.Increment(ref issued) == 1 ? TestJwt : secondJwt),
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        var summary = await service.SyncNowAsync();

        Assert.Equal(1, summary.Synchronized);
        Assert.Equal(0, summary.Pending);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, handler.AuthCalls);
        Assert.Equal(new[] { TestJwt, secondJwt }, seen);
    }

    [Fact]
    public async Task RepeatedHttp401DoesNotLoopAndLeavesTheQueueRecoverable()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized))
        {
            AuthResponder = _ => TokenResponse(MakeJwt(DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid().ToString("N"))),
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        var summary = await service.SyncNowAsync();

        Assert.Equal(0, summary.Synchronized);
        Assert.Equal(2, handler.AuthCalls);
        Assert.Equal(1, summary.Failed + summary.Pending);
    }

    [Fact]
    public async Task TokenFailureStopsSynchronizationWithoutProtectedRequests()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var protectedCalls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            protectedCalls++;
            return new HttpResponseMessage(HttpStatusCode.Created);
        })
        {
            AuthResponder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        var summary = await service.SyncNowAsync();

        Assert.Equal(0, protectedCalls);
        Assert.Equal(1, summary.Pending);
        Assert.Equal(ApiAuthState.Failed, service.AuthStatus.State);
        Assert.Contains("HTTP 500", summary.Message);
    }

    [Fact]
    public async Task NoRequestIsMadeWhenTheMockEndpointIsNotAuthorizedForProduction()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created));
        var requests = 0;
        using var client = new HttpClient(new CountingHandler(handler, () => requests++));
        using var service = new ApiSyncService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client, mockAuthEndpointAuthorized: false);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        var summary = await service.SyncNowAsync();
        var test = await service.TestConnectionAsync();

        Assert.Equal(0, requests);
        Assert.Equal(1, summary.Pending);
        Assert.Contains("disabled", summary.Message);
        Assert.Contains("disabled", test);
        Assert.Equal(ApiAuthState.Failed, service.AuthStatus.State);
    }

    [Fact]
    public async Task ConnectionTestUsesDocumentedReadOnlyRequestAndRedactsSecretsFromDiagnostics()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        HttpMethod? method = null;
        string? pathAndQuery = null;
        var diagnostics = new List<ApiOperationLogEntry>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            method = request.Method;
            pathAndQuery = request.RequestUri!.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ready\",\"access_token\":\"response-secret\"}"),
            };
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        service.DiagnosticReported += diagnostics.Add;

        var result = await service.TestConnectionAsync();

        Assert.Contains("HTTP 200", result);
        Assert.Equal(HttpMethod.Get, method);
        Assert.EndsWith("/api/v1/replay-files?page=1&page_size=1", pathAndQuery);
        var log = File.ReadAllText(Path.Combine(folder.Path, "api-sync.log"));
        Assert.DoesNotContain(TestJwt, log);
        Assert.DoesNotContain("response-secret", log);
        var joined = string.Join(" ", diagnostics.Select(entry => $"{entry.Description} {entry.RequestBody} {entry.ResponseSummary} {entry.ErrorDetails}"));
        Assert.DoesNotContain(TestJwt, joined);
        Assert.DoesNotContain("response-secret", joined);
        Assert.Contains("ready", joined);
    }

    [Fact]
    public async Task ConnectionTestReportsAuthenticationRejectionWithoutClaimingSuccess()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var diagnostics = new List<ApiOperationLogEntry>();
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"message\":\"invalid token\"}") });
        using var client = new HttpClient(handler);
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        service.DiagnosticReported += diagnostics.Add;

        var result = await service.TestConnectionAsync();

        Assert.Contains("rejected", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTTP 401", result);
        Assert.Contains(diagnostics, entry => entry.Severity == ApiDiagnosticSeverity.Error && entry.HttpStatusCode == 401);
    }

    [Fact]
    public async Task InvalidUrlPreventsAnyRequest()
    {
        using var folder = new TemporaryFolder();
        var configPath = Path.Combine(folder.Path, "api-config.json");
        File.WriteAllText(configPath, """{"enabled":true,"baseUrl":"not-a-url"}""");
        var configStore = new ApiIntegrationConfigStore(configPath);
        var requests = 0;
        using var client = new HttpClient(new CountingHandler(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), () => requests++));
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);

        var result = await service.TestConnectionAsync();

        Assert.Contains("configuration", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ConnectionTestReportsTimeoutAsRetryable()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var diagnostics = new List<ApiOperationLogEntry>();
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) => throw new TaskCanceledException("request timed out")));
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        service.DiagnosticReported += diagnostics.Add;

        var result = await service.TestConnectionAsync();

        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostics, entry => entry.Severity == ApiDiagnosticSeverity.Error && entry.IsRetryable == true);
    }

    [Fact]
    public async Task TransientServerFailureRemainsPendingAndSurvivesRestart()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var queuePath = Path.Combine(folder.Path, "api-sync-queue.json");
        using var failingClient = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using (var service = CreateService(configStore, queuePath, failingClient))
        {
            await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));
            var summary = await service.SyncNowAsync();

            Assert.Equal(0, summary.Synchronized);
            Assert.Equal(1, summary.Pending);
            Assert.Equal(0, summary.Failed);
        }

        using var restartedClient = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created)));
        using var restarted = CreateService(configStore, queuePath, restartedClient);

        Assert.Equal(1, restarted.State.Pending);
        var retry = await restarted.SyncNowAsync();
        Assert.Equal(1, retry.Synchronized);
        Assert.Equal(0, retry.Pending);
    }

    [Fact]
    public async Task PermanentRejectionIsMarkedFailedAndCanBeRetriedManually()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var call = 0;
        using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Interlocked.Increment(ref call) == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.Created)));
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));

        var rejected = await service.SyncNowAsync();
        Assert.Equal(1, rejected.Failed);
        Assert.Equal(0, rejected.Pending);

        var retried = await service.SyncNowAsync();
        Assert.Equal(1, retried.Synchronized);
        Assert.Equal(0, retried.Failed);
    }

    [Fact]
    public async Task RateLimitRetryAfterIsPersistedAndProcessingIsRecoveredOnRestart()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var queuePath = Path.Combine(folder.Path, "api-sync-queue.json");
        using var rateLimitedClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(3));
            return response;
        }));
        using (var service = CreateService(configStore, queuePath, rateLimitedClient))
        {
            await service.QueueReplayAsync(CreateMatch(), WriteReplay(folder.Path));
            var summary = await service.SyncNowAsync();
            Assert.Equal(1, summary.Pending);
        }

        var queue = JsonNode.Parse(File.ReadAllText(queuePath))!.AsArray();
        var retryAt = queue[0]!["nextAttemptUtc"]!.GetValue<DateTimeOffset>();
        Assert.True(retryAt >= DateTimeOffset.UtcNow.AddMinutes(2));
        queue[0]!["state"] = "processing";
        File.WriteAllText(queuePath, queue.ToJsonString());

        using var restartedClient = new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Created)));
        using var restarted = CreateService(configStore, queuePath, restartedClient);

        Assert.Equal(1, restarted.State.Pending);
        var summaryAfterRecovery = await restarted.SyncNowAsync();
        Assert.Equal(1, summaryAfterRecovery.Synchronized);
    }

    [Fact]
    public async Task ReprocessingWithANewerAnalyzerVersionGetsANewIdempotencyEntry()
    {
        using var folder = new TemporaryFolder();
        var configStore = new ApiIntegrationConfigStore(Path.Combine(folder.Path, "api-config.json"));
        var bodies = new List<string>();
        using var client = new HttpClient(new StubHttpMessageHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/processing-attempts", StringComparison.Ordinal))
            {
                bodies.Add(body);
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));
        using var service = CreateService(configStore, Path.Combine(folder.Path, "api-sync-queue.json"), client);
        var replay = WriteReplay(folder.Path);
        var match = CreateMatch();
        await service.QueueReplayAsync(match, replay);
        await service.SyncNowAsync();

        match.AnalyzerVersion++;
        await service.QueueReplayAsync(match, replay);

        Assert.Equal(1, service.State.Pending);
        await service.SyncNowAsync();
        Assert.Equal(2, bodies.Count);
        using var first = JsonDocument.Parse(bodies[0]);
        using var second = JsonDocument.Parse(bodies[1]);
        Assert.NotEqual(
            first.RootElement.GetProperty("idempotency_key").GetString(),
            second.RootElement.GetProperty("idempotency_key").GetString());
    }

    // ---- Helpers ----

    private static ApiSyncService CreateService(ApiIntegrationConfigStore store, string queuePath, HttpClient client) =>
        new(store, queuePath, client, mockAuthEndpointAuthorized: true);

    private static DashboardViewModel CreateDashboard(
        string folder,
        ApiSyncService apiSync,
        Action<string> openExternalLink,
        out ChestCounter.Monitor monitor,
        bool hasIdentity = true,
        bool hasCachedIdentity = true,
        string? playerName = "Player",
        string? epicId = "epic-id",
        DateTimeOffset? cachedAt = null)
    {
        var store = new Store(folder);
        var config = new Config();
        if (hasCachedIdentity)
        {
            config.FortCardsPlayerIdentities["epic:EPIC-ID"] = new FortCardsPlayerIdentity(170, cachedAt ?? DateTimeOffset.UtcNow);
        }
        var data = new TrackerData();
        var tracker = new Tracker(store, config, data);
        var services = new MonitorServices();
        var dependencies = new MonitorDependencies(services, services, services, services, services, services);
        monitor = new ChestCounter.Monitor(store, config, data, tracker, folder,
            new NativeOverlayHost(Path.Combine(folder, "overlay.png")), dependencies);
        var runtime = new RuntimeContext(monitor, store, tracker, apiSync, false, Array.Empty<FileInfo>());
        var dashboard = new DashboardViewModel(runtime, openExternalLink);
        var overlay = OverlayModel.Build(data, null, false, DateTime.UtcNow);
        var state = new MonitorState(overlay, Array.Empty<Profile>(), "epic-id", null, null, false, ChestStats.Empty)
        {
            CurrentEpicId = hasIdentity ? epicId : null,
            CurrentPlayerName = hasIdentity ? playerName : null,
        };
        typeof(DashboardViewModel)
            .GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dashboard, state);
        return dashboard;
    }

    private static string PlayerPage(params (long Id, string? EpicId, string? Name)[] players) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            data = players.Select(player => new
            {
                id = player.Id,
                epic_id = player.EpicId,
                bot_id = (string?)null,
                platform_unique_net_id = (string?)null,
                latest_player_name = player.Name,
                created_at = "2026-11-24T00:00:00Z",
                updated_at = "2026-11-24T00:00:00Z",
            }),
            pagination = new { page = 1, page_size = 100, total = players.Length, total_pages = players.Length == 0 ? 0 : 1 },
        });

    private static string SearchPage(params (long Id, string Name)[] players) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                totals = new { },
                items = players.Select(player => new { player_id = player.Id, display_name = player.Name }),
                total = players.Length,
                page = 1,
                page_size = 100,
            },
        });

    private static ApiAuthService CreateAuth(HttpClient client, out Func<DateTimeOffset> clock, Func<DateTimeOffset>? now = null)
    {
        clock = now ?? (() => DateTimeOffset.UtcNow);
        return new ApiAuthService((request, ct) => client.SendAsync(request, ct), mockEndpointAuthorized: true, clock);
    }

    private static HttpResponseMessage TokenResponse(string jwt) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            success = true,
            token = jwt,
            user = new { id = "123e4567-e89b-12d3-a456-426614174000", name = "Example User", username = "Example", role = "admin" },
        })),
    };

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string MakeJwt(DateTimeOffset expires, string id) =>
        $"{Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}.{Base64Url($"{{\"sub\":\"{id}\",\"exp\":{expires.ToUnixTimeSeconds()}}}")}.signature";

    private static string MakeJwtWithoutExp() =>
        $"{Base64Url("{\"alg\":\"HS256\"}")}.{Base64Url("{\"sub\":\"1\"}")}.signature";

    private static FileInfo WriteReplay(string folder)
    {
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".replay");
        File.WriteAllText(path, "replay file for queue test");
        return new FileInfo(path);
    }

    private static MatchRecord CreateMatch() => new()
    {
        SessionId = "match-session",
        PlayerName = "Player",
        IsReplayOwner = true,
        MatchStartUtc = DateTime.UtcNow,
        RawReplayData = CreateRawSnapshot(),
    };

    private static ReplayRawData CreateRawSnapshot() =>
        JsonSerializer.Deserialize<ReplayRawData>(
            """{"parseMode":"EventsOnly","match":null,"players":null,"teams":[],"eventEliminations":[],"killFeed":[],"map":null,"weapons":null,"observations":null}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Authorization, string Body);

    // Answers POST auth/mock itself so tests only describe the protected endpoints.
    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _authCalls;
        private int _protectedCalls;
        public int AuthCalls => _authCalls;
        public int ProtectedCalls => _protectedCalls;
        public string? LastAuthAuthorization { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? AuthResponder { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ProtectedResponderAsync { get; set; }
        public TimeSpan AuthDelay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/auth/mock", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _authCalls);
                LastAuthAuthorization = request.Headers.Authorization?.ToString();
                if (AuthDelay > TimeSpan.Zero)
                {
                    await Task.Delay(AuthDelay, cancellationToken);
                }
                return AuthResponder?.Invoke(request) ?? TokenResponse(TestJwt);
            }

            Interlocked.Increment(ref _protectedCalls);
            if (ProtectedResponderAsync is { } protectedResponder)
            {
                return await protectedResponder(request, cancellationToken);
            }

            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, body);
        }

        public Task<HttpResponseMessage> InvokeAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken);
    }

    private sealed class SingleUseResponseContent(string body) : HttpContent
    {
        private readonly MemoryStream _stream = new(Encoding.UTF8.GetBytes(body), writable: false);
        private bool _streamConsumed;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            if (_streamConsumed)
            {
                throw new InvalidOperationException("The stream was already consumed. It cannot be read again.");
            }

            _streamConsumed = true;
            return _stream.CopyToAsync(stream);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            if (_streamConsumed)
            {
                throw new InvalidOperationException("The stream was already consumed. It cannot be read again.");
            }

            _streamConsumed = true;
            return Task.FromResult<Stream>(_stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingHandler(StubHttpMessageHandler inner, Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return inner.InvokeAsync(request, cancellationToken);
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChestCounter.ApiSync.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
