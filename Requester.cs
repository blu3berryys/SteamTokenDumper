﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

#pragma warning disable CA1031 // Do not catch general exception types
namespace SteamTokenDumper;

internal class Requester
{
    private const int ItemsPerRequest = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private readonly Payload payload;
    private readonly SteamApps steamApps;
    private readonly Configuration config;
    private readonly HashSet<uint> skippedPackages = new();
    private readonly HashSet<uint> skippedApps = new();
    private bool SomeRequestFailed;

    public Requester(Payload payload, SteamApps steamApps, Configuration config)
    {
        this.payload = payload;
        this.steamApps = steamApps;
        this.config = config;
    }

    public List<SteamApps.PICSRequest> ProcessLicenseList(SteamApps.LicenseListCallback licenseList)
    {
        var packages = new List<SteamApps.PICSRequest>();

        foreach (var license in licenseList.LicenseList)
        {
            packages.Add(new SteamApps.PICSRequest(license.PackageID, license.AccessToken));

            // Request autogrant packages so we can automatically skip all apps inside of it
            if (config.SkipAutoGrant && license.PaymentMethod == EPaymentMethod.AutoGrant)
            {
                skippedPackages.Add(license.PackageID);
                continue;
            }

            if (license.AccessToken == 0)
            {
                continue;
            }

            payload.Subs[license.PackageID.ToString(CultureInfo.InvariantCulture)] = license.AccessToken.ToString(CultureInfo.InvariantCulture);
        }

        if (skippedPackages.Any())
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Skipped auto granted packages: {string.Join(", ", skippedPackages)}");
            Console.ResetColor();
        }

        return packages;
    }

    public async Task ProcessPackages(List<SteamApps.PICSRequest> packages)
    {
        Console.WriteLine();
        Console.WriteLine($"You have {packages.Count} licenses ({packages.Count(x => x.AccessToken != 0)} of them have a token)");
        Console.WriteLine();

        try
        {
            var apps = await RequestPackageInfo(packages);
            await Request(apps);
        }
        catch (Exception e)
        {
            SomeRequestFailed = true;

            await Console.Error.WriteLineAsync();
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync(e.ToString());
            Console.ResetColor();
        }

        if (SomeRequestFailed)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync("[!]");
            await Console.Error.WriteLineAsync("[!] Some of the requests to Steam failed, which may have resulted in");
            await Console.Error.WriteLineAsync("[!] some of the tokens or depot keys not being fetched.");
            await Console.Error.WriteLineAsync("[!] You can try running the dumper again later.");
            await Console.Error.WriteLineAsync("[!]");
            await Console.Error.WriteLineAsync();
            Console.ResetColor();
        }
    }

    private async Task<HashSet<uint>> RequestPackageInfo(List<SteamApps.PICSRequest> subInfoRequests)
    {
        var apps = new HashSet<uint>();

        foreach (var chunk in subInfoRequests.Chunk(ItemsPerRequest))
        {
            AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet info = null;

            for (var retry = 3; retry > 0; retry--)
            {
                try
                {
                    var infoTask = steamApps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), chunk);
                    infoTask.Timeout = Timeout;
                    info = await infoTask;
                    break;
                }
                catch (Exception e)
                {
                    ConsoleRewriteLine($"Package info task failed: {e.GetType()} {e.Message}");

                    await AwaitReconnectIfDisconnected();
                }
            }

            if (info == null)
            {
                SomeRequestFailed = true;
                continue;
            }

            if (info.Results == null)
            {
                continue;
            }

            foreach (var result in info.Results)
            {
                foreach (var package in result.Packages.Values)
                {
                    var skipAutoGrant = skippedPackages.Contains(package.ID);

                    foreach (var id in package.KeyValues["appids"].Children)
                    {
                        var appid = id.AsUnsignedInteger();

                        if (skipAutoGrant)
                        {
                            skippedApps.Add(appid);
                            continue;
                        }

                        if (config.SkipApps.Contains(appid))
                        {
                            skippedApps.Add(appid);
                            continue;
                        }

                        apps.Add(appid);
                    }
                }
            }

            ConsoleRewriteLine($"You own {apps.Count} apps");
        }

        foreach (var appid in config.SkipApps)
        {
            if (payload.Apps.Remove(appid.ToString(CultureInfo.InvariantCulture)))
            {
                skippedApps.Add(appid);
            }
        }

        // Remove all apps that may have been received from other packages
        foreach (var appid in skippedApps)
        {
            payload.Apps.Remove(appid.ToString(CultureInfo.InvariantCulture));
        }

        if (skippedApps.Any())
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Skipped app ids: {string.Join(", ", skippedApps)}");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine();

        return apps;
    }

    private async Task Request(HashSet<uint> apps)
    {
        var appInfoRequests = new List<SteamApps.PICSRequest>();
        var tokensCount = 0;
        var tokensDeniedCount = 0;
        var tokensNonZeroCount = 0;

        foreach (var chunk in apps.Chunk(ItemsPerRequest))
        {
            SteamApps.PICSTokensCallback tokens = null;

            for (var retry = 3; retry > 0; retry--)
            {
                try
                {
                    var tokensTask = steamApps.PICSGetAccessTokens(chunk, Enumerable.Empty<uint>());
                    tokensTask.Timeout = Timeout;
                    tokens = await tokensTask;
                    break;
                }
                catch (Exception e)
                {
                    ConsoleRewriteLine($"App token task failed: {e.GetType()} {e.Message}");

                    await AwaitReconnectIfDisconnected();
                }
            }

            if (tokens == null)
            {
                SomeRequestFailed = true;
                continue;
            }

            tokensCount += tokens.AppTokens.Count;
            tokensDeniedCount += tokens.AppTokensDenied.Count;
            tokensNonZeroCount += tokens.AppTokens.Count(x => x.Value > 0);

            ConsoleRewriteLine($"App tokens granted: {tokensCount} - Denied: {tokensDeniedCount} - Non-zero: {tokensNonZeroCount}");

            if (config.SkipDepotKeys)
            {
                continue;
            }

            foreach (var (key, value) in tokens.AppTokens)
            {
                if (value > 0)
                {
                    payload.Apps[key.ToString(CultureInfo.InvariantCulture)] = value.ToString(CultureInfo.InvariantCulture);
                }

                appInfoRequests.Add(new SteamApps.PICSRequest(key, value));
            }
        }

        Console.WriteLine();

        if (appInfoRequests.Count > 0)
        {
            Console.WriteLine();

            var loops = 0;
            var total = (-1L + appInfoRequests.Count + ItemsPerRequest) / ItemsPerRequest;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = ItemsPerRequest };

            foreach (var chunk in appInfoRequests.Chunk(ItemsPerRequest))
            {
                ConsoleRewriteLine($"App info request {++loops} of {total} - {payload.Depots.Count} depot keys - Waiting for appinfo...");

                AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet appInfo = null;

                for (var retry = 3; retry > 0; retry--)
                {
                    try
                    {
                        var appJob = steamApps.PICSGetProductInfo(chunk, Enumerable.Empty<SteamApps.PICSRequest>());
                        appJob.Timeout = Timeout;
                        appInfo = await appJob;
                        break;
                    }
                    catch (Exception e)
                    {
                        ConsoleRewriteLine($"App info task failed: {e.GetType()} {e.Message}");

                        await AwaitReconnectIfDisconnected();
                    }
                }

                if (appInfo == null)
                {
                    SomeRequestFailed = true;
                    continue;
                }

                if (appInfo.Results == null)
                {
                    continue;
                }

                var depotsToRequest = new HashSet<(uint DepotID, uint AppID)>();

                foreach (var app in chunk)
                {
                    depotsToRequest.Add((app.ID, app.ID));
                }

                foreach (var result in appInfo.Results)
                {
                    foreach (var app in result.Apps.Values)
                    {
                        foreach (var depot in app.KeyValues["depots"].Children)
                        {
                            var depotfromapp = depot["depotfromapp"].AsUnsignedInteger();

                            // common redistributables and steam sdk
                            if (depotfromapp == 1007 || depotfromapp == 228980)
                            {
                                continue;
                            }

                            if (!uint.TryParse(depot.Name, out var depotid))
                            {
                                continue;
                            }

                            var dlcappid = depot["dlcappid"].AsUnsignedInteger();

                            if (skippedApps.Contains(dlcappid))
                            {
                                continue;
                            }

                            if (payload.Depots.ContainsKey(depot.Name!))
                            {
                                continue;
                            }

                            depotsToRequest.Add((depotid, app.ID));
                        }
                    }
                }

                ConsoleRewriteLine($"App info request {loops} of {total} - {payload.Depots.Count} depot keys - Waiting for 0/{depotsToRequest.Count} keys...");

                var processedKeys = 0;
                var depotKeys = new ConcurrentDictionary<string, string>();

                await Parallel.ForEachAsync(
                    depotsToRequest,
                    parallelOptions,
                    async (depot, token) =>
                    {
                        try
                        {
                            var job = steamApps.GetDepotDecryptionKey(depot.DepotID, depot.AppID);
                            job.Timeout = Timeout;
                            var result = await job;

                            if (result.Result == EResult.OK)
                            {
                                var key = Convert.ToHexString(result.DepotKey);
                                depotKeys[result.DepotID.ToString(CultureInfo.InvariantCulture)] = key;
                            }

                            var count = ++processedKeys;

                            if (count % ItemsPerRequest == 0)
                            {
                                ConsoleRewriteLine($"App info request {loops} of {total} - {payload.Depots.Count} depot keys - Waiting for {count}/{depotsToRequest.Count} keys...");
                            }
                        }
                        catch
                        {
                            SomeRequestFailed = true;
                        }
                    }
                );

                foreach (var (key, value) in depotKeys)
                {
                    payload.Depots[key] = value;
                }

                if (!Program.IsConnected)
                {
                    await Program.ReconnectEvent.Task;
                }
            }

            appInfoRequests.Clear();
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Sub tokens: {payload.Subs.Count}");
        Console.WriteLine($"App tokens: {payload.Apps.Count}");
        Console.WriteLine($"Depot keys: {payload.Depots.Count}");
        Console.ResetColor();
    }

    private static async Task AwaitReconnectIfDisconnected()
    {
        if (Program.IsConnected)
        {
            await Task.Delay(200 + Random.Shared.Next(1001));
            return;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        await Console.Error.WriteLineAsync("[!] Disconnected from Steam while requesting, will continue after logging in again.");
        Console.ResetColor();

        await Program.ReconnectEvent.Task;
    }

    private static void ConsoleRewriteLine(string text)
    {
        Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r{text}");
    }
}
