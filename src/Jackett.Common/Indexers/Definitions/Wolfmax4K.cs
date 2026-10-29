using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using BencodeNET.Parsing;
using Jackett.Common.Extensions;
using Jackett.Common.Helpers;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using static Jackett.Common.Models.IndexerConfig.ConfigurationData;
using WebClient = Jackett.Common.Utils.Clients.WebClient;
using ParseUtil = Jackett.Common.Utils.ParseUtil;

namespace Jackett.Common.Indexers.Definitions
{
    public sealed class WolfmaxDownloadException : Exception
    {
        public WolfmaxDownloadException(string message) : base(message) { }
    }

    [ExcludeFromCodeCoverage]
    public class Wolfmax4K : IndexerBase
    {
        public override string Id => "wolfmax4k";
        public override string Name => "WolfTorrent";
        public override string Description => "WolfTorrent is a SPANISH Public site for MOVIES / TV. Downloads require Byparr.";

        public override string SiteLink { get; protected set; } = "https://wolftorrent.com/";
        public override string[] LegacySiteLinks => new[]
        {
            "https://wolfmax4k.com/",
        };

        public override string Language => "es-ES";
        public override string Type => "public";

        public override TorznabCapabilities TorznabCaps => SetCapabilities();

        private const string TorrentLinkEncryptionKey = "fee631d2cffda38a78b96ee6d2dfb43a";
        private static readonly System.Threading.SemaphoreSlim DownloadGate = new System.Threading.SemaphoreSlim(1, 1);
        private string ByparrUrl => ((StringConfigurationItem)configData.GetDynamic("byparrurl")).Value;

        private static Dictionary<string, long> EstimatedSizeByCategory => new Dictionary<string, long>
        {
            { Wolfmax4KCatType.Pelicula, 2.Gigabytes() },
            { Wolfmax4KCatType.Pelicula720, 5.Gigabytes() },
            { Wolfmax4KCatType.Pelicula1080, 15.Gigabytes() },
            { Wolfmax4KCatType.Pelicula4K, 30.Gigabytes() },
            { Wolfmax4KCatType.Serie, 512.Megabytes() },
            { Wolfmax4KCatType.Serie720, 1.Gigabytes() },
            { Wolfmax4KCatType.Serie1080, 3.Gigabytes() },
            { Wolfmax4KCatType.Serie4K, 8.Gigabytes() }
        };

        public Wolfmax4K(IIndexerConfigurationService configService, WebClient w, Logger l, IProtectionService ps,
                         ICacheService cs)
            : base(configService: configService,
                   client: w,
                   logger: l,
                   p: ps,
                   cacheService: cs,
                   configData: new ConfigurationData())
        {
            configData.AddDynamic("byparrurl", new StringConfigurationItem("Byparr URL") { Value = "http://127.0.0.1:8192" });
            configData.AddDynamic("byparrinfo", new DisplayInfoConfigurationItem("Byparr", "Downloads require a running Byparr service. Configure its proxy to use the same network exit as Jackett. In containers, use the service hostname instead of localhost."));
            webclient.requestDelay = 0.5;
            webclient.EmulateBrowser = false;
        }

        private static TorznabCapabilities SetCapabilities()
        {
            var caps = new TorznabCapabilities
            {
                TvSearchParams = new List<TvSearchParam>
                {
                    TvSearchParam.Q, TvSearchParam.Season, TvSearchParam.Ep
                },
                MovieSearchParams = new List<MovieSearchParam>
                {
                    MovieSearchParam.Q
                }
            };
            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Pelicula, TorznabCatType.MoviesSD, "Peliculas");
            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Pelicula720, TorznabCatType.Movies, "Peliculas 720p");
            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Pelicula1080, TorznabCatType.MoviesHD, "Peliculas 1080p");
            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Pelicula4K, TorznabCatType.MoviesUHD, "Peliculas 4k");

            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Serie, TorznabCatType.TVSD, "Series");
            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Serie720, TorznabCatType.TV, "Series 720p");
            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Serie1080, TorznabCatType.TVHD, "Series 1080p");
            caps.Categories.AddCategoryMapping(Wolfmax4KCatType.Serie4K, TorznabCatType.TVUHD, "Series 4k");

            return caps;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);

            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Any(), () =>
                                    throw new Exception("Could not find releases from this URL"));

            return IndexerConfigurationStatus.Completed;
        }

        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            query = SanitizeTorznabQuery(query);

            // without search term the site returns the latest releases
            var searchUrl = query.SearchTerm.IsNullOrWhiteSpace()
                ? SiteLink + "ultimos"
                : SiteLink + "buscar/" + Uri.EscapeDataString(query.SearchTerm);

            var result = await RequestWithCookiesAndRetryAsync(searchUrl, referer: SiteLink);
            if (result.Status != HttpStatusCode.OK)
                throw new ExceptionWithConfigData(result.ContentString, configData);

            try
            {
                var parser = new HtmlParser();
                using var dom = parser.ParseDocument(result.ContentString);

                var items = new List<Wolfmax4KItem>();
                foreach (var card in dom.QuerySelectorAll("article.wolf-card"))
                    items.AddRange(await ParseCardAsync(card, NeedsAllEpisodes(card, query)));

                return items.Select(item => ExtractReleaseInfo(item, query)).ToList()
                            .Where(x => x != null);
            }
            catch (Exception ex)
            {
                OnParseError(result.ContentString, ex);
            }

            return new List<ReleaseInfo>();
        }

        private static bool NeedsAllEpisodes(IElement card, TorznabQuery query)
        {
            // the card of a tv show only has the last episodes, the rest are hidden behind "Ver los N episodios restantes"
            if (card.QuerySelector("details[data-wolf-episodes]") == null || query.SearchTerm.IsNullOrWhiteSpace() || query.IsMovieSearch)
                return false;

            // skip the seasons that will be filtered anyway
            var title = card.QuerySelector("a.wolf-card-main")?.TextContent ?? "";
            var cardSeason = Regex.Match(title, @"(\d+)ª\s+Temporada");
            return !query.Season.HasValue || !cardSeason.Success || int.Parse(cardSeason.Groups[1].Value) == query.Season;
        }

        private async Task<IEnumerable<Wolfmax4KItem>> ParseCardAsync(IElement card, bool fetchAllEpisodes)
        {
            // <a class="wolf-card-main" href="/serie/86a2xh">Preacher - 1ª Temporada</a>
            var mainLink = card.QuerySelector("a.wolf-card-main");
            var detailsPath = mainLink?.GetAttribute("href");
            if (detailsPath.IsNullOrWhiteSpace())
                return Enumerable.Empty<Wolfmax4KItem>();

            var title = mainLink.TextContent.Trim().TrimEnd('.');
            var imagePath = card.QuerySelector(".wolf-card-poster img")?.GetAttribute("data-original");
            var image = imagePath.IsNotNullOrWhiteSpace() ? new Uri(new Uri(SiteLink), imagePath).AbsoluteUri : null;
            // <time datetime="2018-07-03T12:24:16Z">03/07/2018</time>
            var publishDate = ParseDate(card.QuerySelector(".wolf-card-date time")?.GetAttribute("datetime"));

            var files = card.QuerySelectorAll("li.wolf-card-file").ToList();
            if (fetchAllEpisodes)
                files = await GetAllEpisodesAsync(card.QuerySelector("details[data-wolf-episodes]").GetAttribute("data-wolf-episodes")) ?? files;

            // <li class="wolf-card-file">
            //   <a class="wolf-card-format" href="/serie/episodio/86a2xh"><strong>Episodio 1x10 -</strong><span>HDTV</span></a>
            //   <span class="wolf-card-size">614,84 MB</span>
            //   <button class="protected-download" data-content-code="pkghcn" data-tabla="series">
            // movies only have the quality: <strong>4K</strong>
            return files.Select(file =>
            {
                var format = file.QuerySelector(".wolf-card-format");
                var button = file.QuerySelector("button.protected-download");
                return new Wolfmax4KItem
                {
                    Title = title,
                    DetailsPath = format?.GetAttribute("href") ?? detailsPath,
                    EpisodeText = format?.QuerySelector("strong")?.TextContent,
                    Quality = (format?.QuerySelector("span") ?? format?.QuerySelector("strong"))?.TextContent.Trim(),
                    Size = file.QuerySelector(".wolf-card-size")?.TextContent,
                    PublishDate = publishDate,
                    Image = image,
                    ContentCode = button?.GetAttribute("data-content-code"),
                    Tabla = button?.GetAttribute("data-tabla")
                };
            }).ToList();
        }

        private async Task<List<IElement>> GetAllEpisodesAsync(string apiPath)
        {
            // the site loads the hidden episodes of a card with its own api, without "after" it returns all of them
            // <details data-wolf-episodes="/api/episodios?type=serie&id=715963&after=716195&calidad=">
            // {"success":true,"html":"<li class=\"wolf-card-file\">...</li>...","next_after":null}
            var apiUrl = new Uri(new Uri(SiteLink), Regex.Replace(apiPath, @"&after=\d+", "")).AbsoluteUri;
            var headers = new Dictionary<string, string> { { "X-Requested-With", "XMLHttpRequest" } };
            var files = new List<IElement>();
            var parser = new HtmlParser();

            for (var after = ""; after != null;)
            {
                var result = await RequestWithCookiesAndRetryAsync(apiUrl + after, referer: SiteLink, headers: headers);
                var json = result.Status == HttpStatusCode.OK ? JObject.Parse(result.ContentString) : null;
                if (json == null || !json.Value<bool>("success"))
                {
                    logger.Warn($"{Id}: could not load the episodes from {apiUrl}, only the visible ones will be returned");
                    return null;
                }

                files.AddRange(parser.ParseDocument(json.Value<string>("html")).QuerySelectorAll("li.wolf-card-file"));
                var nextAfter = json.Value<string>("next_after");
                after = nextAfter.IsNullOrWhiteSpace() ? null : "&after=" + nextAfter;
            }

            return files;
        }

        public override async Task<byte[]> Download(Uri link)
        {
            try { return await DownloadPreparedTorrentAsync(link); }
            catch (WolfmaxDownloadException) { throw; }
            catch (Exception ex)
            {
                logger.Warn(ex, "WolfTorrent browser download failed");
                throw new WolfmaxDownloadException("No se pudo completar la descarga de WolfTorrent. Revisa el registro local de Jackett para ver la causa.");
            }
        }

        private async Task<byte[]> DownloadPreparedTorrentAsync(Uri link)
        {
            var parameters = HttpUtility.ParseQueryString(link.Query);

            var contentCode = parameters["code"];
            var contentType = parameters["tabla"] switch
            {
                "peliculas" => "pelicula",
                "series" => "serie",
                "documentales" => "documental",
                "animaciones" => "animacion",
                "telenovelas" => "telenovela",
                _ => null
            };
            if (contentType == null || contentCode == null || !Regex.IsMatch(contentCode, @"\A[a-z2-9]{1,7}\z"))
                throw new WolfmaxDownloadException("El resultado de WolfTorrent tiene un identificador de descarga no válido. Repite la búsqueda.");

            var gateway = new Uri(new Uri(SiteLink), "descarga/" + contentType + "/" + contentCode);
            string html;
            if (!await DownloadGate.WaitAsync(TimeSpan.FromSeconds(5)))
                throw new WolfmaxDownloadException("Hay otra descarga de WolfTorrent en curso. Espera a que termine y vuelve a intentarlo.");
            try { html = await ReadByparrPageAsync(gateway, ByparrUrl); }
            finally { DownloadGate.Release(); }
            var target = await ResolvePreparedTorrentAsync(html, new Uri(SiteLink));
            var response = await RequestWithCookiesAsync(target.AbsoluteUri, cookieOverride: string.Empty, referer: SiteLink);
            if (response.Status != HttpStatusCode.OK || response.IsRedirect)
                throw new WolfmaxDownloadException("WolfTorrent no entregó el torrent esperado (HTTP " + (int)response.Status + ").");
            var torrent = response.ContentBytes;
            if (torrent == null || torrent.Length == 0 || torrent.Length > 8 * 1024 * 1024)
                throw new WolfmaxDownloadException("WolfTorrent devolvió un torrent vacío o demasiado grande.");
            try
            {
                var parsed = new BencodeNET.Parsing.BencodeParser().Parse(torrent) as BencodeNET.Objects.BDictionary;
                if (parsed == null || !parsed.ContainsKey("info") || !(parsed["info"] is BencodeNET.Objects.BDictionary info) ||
                    !info.ContainsKey("name") || !info.ContainsKey("pieces") || !info.ContainsKey("piece length"))
                    throw new FormatException();
            }
            catch (Exception)
            {
                throw new WolfmaxDownloadException("WolfTorrent devolvió datos que no son un torrent válido.");
            }
            return torrent;
        }

        private static async Task<string> ReadByparrPageAsync(Uri gateway, string service)
        {
            if (!Uri.TryCreate(service, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
                throw new WolfmaxDownloadException("La URL de Byparr para WolfTorrent no es válida.");

            using var handler = new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(90),
                MaxResponseContentBufferSize = 16 * 1024 * 1024
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, service.TrimEnd('/') + "/v1");
            request.Content = new StringContent(JsonConvert.SerializeObject(new
            {
                cmd = "request.get", url = gateway.AbsoluteUri, maxTimeout = 55000
            }), Encoding.UTF8, "application/json");
            try
            {
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    throw new WolfmaxDownloadException("Byparr no pudo preparar la descarga de WolfTorrent (HTTP " + (int)response.StatusCode + ").");
                return ParseByparrResponse(await response.Content.ReadAsStringAsync());
            }
            catch (HttpRequestException)
            {
                throw new WolfmaxDownloadException("No se pudo conectar con Byparr. Comprueba su URL en la configuración del indexador y que el servicio esté disponible.");
            }
            catch (TaskCanceledException)
            {
                throw new WolfmaxDownloadException("Byparr agotó el tiempo disponible para preparar la descarga de WolfTorrent.");
            }
        }

        public static string ParseByparrResponse(string json)
        {
            try
            {
                var result = JObject.Parse(json);
                var solution = result["solution"] as JObject;
                var html = (string)solution?["response"];
                if ((string)result["status"] == "ok" && (int?)solution?["status"] == 200 &&
                    Uri.TryCreate((string)solution?["url"], UriKind.Absolute, out var final) &&
                    final.Scheme == "https" && final.Host == "enlacito.com" && final.Port == 443 &&
                    string.IsNullOrEmpty(final.UserInfo) && !string.IsNullOrWhiteSpace(html) &&
                    html.Contains("link_out"))
                    return html;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is ArgumentException || ex is FormatException) { }
            throw new WolfmaxDownloadException("Byparr no ha obtenido el enlace preparado de Enlacito. La verificación de WolfTorrent puede seguir pendiente o haber fallado.");
        }

        public static async Task<Uri> ResolvePreparedTorrentAsync(string html, Uri site)
        {
            var match = Regex.Match(html, @"\bvar\s+link_out\s*=\s*[""']([A-Za-z0-9+/=]+)[""']");
            if (!match.Success)
                throw new WolfmaxDownloadException("Enlacito no ha incluido un destino de descarga reconocible. Puede haber cambiado el formato de la página.");

            string destination;
            try
            {
                var encrypted = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
                destination = await OpenSSLDecryptAsync(encrypted, TorrentLinkEncryptionKey);
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException || ex is ArgumentException)
            {
                throw new WolfmaxDownloadException("No se pudo interpretar el destino preparado por Enlacito. Puede haber cambiado su formato.");
            }

            if (!IsAllowedTorrentDestination(destination, site, out var target))
                throw new WolfmaxDownloadException("Enlacito devolvió un destino inesperado; no se ha solicitado.");
            return target;
        }

        public static bool IsAllowedTorrentDestination(string destination, Uri site, out Uri target)
        {
            return Uri.TryCreate(destination, UriKind.Absolute, out target) &&
                   target.Scheme == Uri.UriSchemeHttps && target.Host == site.Host &&
                   target.Port == 443 && string.IsNullOrEmpty(target.UserInfo) &&
                   string.IsNullOrEmpty(target.Query) && string.IsNullOrEmpty(target.Fragment) &&
                   target.AbsolutePath.StartsWith("/torrents/", StringComparison.Ordinal) &&
                   target.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        }

        // Thanks to https://stackoverflow.com/a/5454692/2078070 !!!
        private static async Task<string> OpenSSLDecryptAsync(string encrypted, string passphrase)
        {
            // base 64 decode
            var encryptedBytesWithSalt = Convert.FromBase64String(encrypted);
            if (encryptedBytesWithSalt.Length < 32 || Encoding.ASCII.GetString(encryptedBytesWithSalt, 0, 8) != "Salted__")
                throw new FormatException("Invalid encrypted torrent link");

            // extract salt (first 8 bytes of encrypted)
            var salt = new byte[8];
            var encryptedBytes = new byte[encryptedBytesWithSalt.Length - salt.Length - 8];
            Buffer.BlockCopy(encryptedBytesWithSalt, 8, salt, 0, salt.Length);
            Buffer.BlockCopy(encryptedBytesWithSalt, salt.Length + 8, encryptedBytes, 0, encryptedBytes.Length);

            // get key and iv
            DeriveKeyAndIV(passphrase, salt, out var key, out var iv);

            return await DecryptStringFromBytesAesAsync(encryptedBytes, key, iv);
        }

        private static void DeriveKeyAndIV(string passphrase, byte[] salt, out byte[] key, out byte[] iv)
        {
            // generate key and iv
            var concatenatedHashes = new List<byte>(48);

            var password = Encoding.UTF8.GetBytes(passphrase);
            var currentHash = Array.Empty<byte>();
            var md5 = MD5.Create();
            var enoughBytesForKey = false;

            // See http://www.openssl.org/docs/crypto/EVP_BytesToKey.html#KEY_DERIVATION_ALGORITHM
            while (!enoughBytesForKey)
            {
                var preHashLength = currentHash.Length + password.Length + salt.Length;
                var preHash = new byte[preHashLength];

                Buffer.BlockCopy(currentHash, 0, preHash, 0, currentHash.Length);
                Buffer.BlockCopy(password, 0, preHash, currentHash.Length, password.Length);
                Buffer.BlockCopy(salt, 0, preHash, currentHash.Length + password.Length, salt.Length);

                currentHash = md5.ComputeHash(preHash);
                concatenatedHashes.AddRange(currentHash);

                if (concatenatedHashes.Count >= 48)
                {
                    enoughBytesForKey = true;
                }
            }

            key = new byte[32];
            iv = new byte[16];
            concatenatedHashes.CopyTo(0, key, 0, 32);
            concatenatedHashes.CopyTo(32, iv, 0, 16);

            md5.Clear();
            md5 = null;
        }

        private static async Task<string> DecryptStringFromBytesAesAsync(byte[] cipherText, byte[] key, byte[] iv)
        {
            if (cipherText == null || cipherText.Length <= 0)
            {
                throw new ArgumentNullException(nameof(cipherText));
            }

            if (key == null || key.Length <= 0)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (iv == null || iv.Length <= 0)
            {
                throw new ArgumentNullException(nameof(iv));
            }

            using var aesAlg = Aes.Create();
            aesAlg.Key = key;
            aesAlg.IV = iv;

            var decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

            using var msDecrypt = new MemoryStream(cipherText);
            using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
            using var srDecrypt = new StreamReader(csDecrypt);

            return await srDecrypt.ReadToEndAsync();
        }

        private static TorznabQuery SanitizeTorznabQuery(TorznabQuery query)
        {
            // Taken from Dontorrent.cs
            // Eg. Marco.Polo.2014.S02E08

            // the season/episode part is already parsed by Jackett
            // query.SanitizedSearchTerm = Marco.Polo.2014.
            // query.Season = 2
            // query.Episode = 8
            var searchTerm = query.SanitizedSearchTerm;

            // replace punctuation symbols with spaces
            // searchTerm = Marco Polo 2014
            searchTerm = Regex.Replace(searchTerm, @"[-._\(\)@/\\\[\]\+\%]", " ");
            searchTerm = searchTerm.Trim();

            // we parse the year and remove it from search
            // searchTerm = Marco Polo
            // query.Year = 2014
            var r = new Regex("([ ]+([0-9]{4}))$", RegexOptions.IgnoreCase);
            var m = r.Match(searchTerm);
            if (m.Success)
            {
                query.Year = int.Parse(m.Groups[2].Value);
                searchTerm = searchTerm.Replace(m.Groups[1].Value, "");
            }

            // remove some words
            searchTerm = Regex.Replace(searchTerm, @"\b(espa[ñn]ol|spanish|castellano|spa)\b", "", RegexOptions.IgnoreCase);

            query.SearchTerm = searchTerm;
            return query;
        }

        private ReleaseInfo ExtractReleaseInfo(Wolfmax4KItem item, TorznabQuery query)
        {
            // https://wolfmax4k.com/pelicula/5dw2j7
            // https://wolfmax4k.com/serie/episodio/8fmmgy
            // https://wolfmax4k.com/documental/episodio/48jcn5

            var torrentName = item.Title;
            var guid = item.DetailsPath;
            var quality = item.Quality;
            var image = item.Image;

            if (torrentName.IsNullOrWhiteSpace() || guid.IsNullOrWhiteSpace() || quality.IsNullOrWhiteSpace() ||
                item.ContentCode.IsNullOrWhiteSpace())
            {
                // Some torrents has no quality.
                // Ignored it because they are torrents that are not well categorized
                // as this game https://wolfmax4k.com/juego/james-cameronavatar/
                return null;
            }

            quality = ParseQuality(quality);
            var details = new Uri(new Uri(SiteLink), guid);
            // the torrent url is resolved by the download api of the site, see Download()
            var link = new Uri(new Uri(SiteLink), $"api/descargas?tabla={item.Tabla}&code={item.ContentCode}");
            var title = ParseTitle(torrentName, item.EpisodeText, quality);
            var episodes = GetEpisodesFromTitle(title);
            var wolfmaxCategory = ParseCategory(torrentName, guid, quality);

            var releaseInfo = new ReleaseInfo
            {
                Title = title,
                Link = link,
                Details = details,
                Guid = link,
                Category = MapTrackerCatToNewznab(wolfmaxCategory),
                PublishDate = item.PublishDate ?? DateTime.Now,
                Size = item.Size.IsNotNullOrWhiteSpace()
                    ? ParseUtil.GetBytes(item.Size)
                    : EstimatedSizeByCategory[wolfmaxCategory] * Math.Max(episodes.Count, 1),
                Seeders = 1,
                Peers = 2,
                DownloadVolumeFactor = 0,
                UploadVolumeFactor = 1
            };

            if (image.IsNotNullOrWhiteSpace() && !image.Contains("/no-imagen.jpg"))
                releaseInfo.Poster = new Uri(image);

            // Filter by category
            if (query.Categories.Any() && !query.Categories.Intersect(releaseInfo.Category).Any())
            {
                return null;
            }

            // Filter by Season
            if (query.Season.HasValue && !releaseInfo.Title.Contains("S" + query.Season.Value.ToString("D2")))
            {
                return null;
            }

            // Filter by Episode
            if (int.TryParse(query.Episode, out var episode) && episodes.Any() && !episodes.Contains(episode))
            {
                return null;
            }

            return releaseInfo;
        }

        private string ParseTitle(string torrentName, string episodeText, string quality)
        {
            var title = Regex.Replace(torrentName, @"(\- )?\(?\d+ª Temporada\)?", "");
            title = Regex.Replace(title, @"\[(Esp|Spanish)\]", "", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\(?wolfmax4k\.com\)?", "", RegexOptions.IgnoreCase);

            var seasonEpisode = ParseSeasonAndEpisode(episodeText);
            if (seasonEpisode.IsNotNullOrWhiteSpace())
            {
                // only replace Cap. if it could be parsed
                title = Regex.Replace(title, @"\[Cap\.(\s+)?(\d+)\]", "").Trim();
                title += " " + seasonEpisode;
            }

            // remove the "quality" from the torrentName and
            // adds it from the "quality" field of the api
            title = Regex.Replace(title, @"\s*\[(.*)(HDTV|Bluray|4k|DVDRIP|\d{3,4}p)(.*)\]", "",
                                  RegexOptions.IgnoreCase);

            title = title + " [" + quality + "] SPANISH";

            return title.Trim();
        }

        private string ParseCategory(string torrentName, string guid, string quality)
        {
            // If the url contains "/serie" or "/episodio/"
            // or contains "Cap." in the torrentName it's a tv show
            // If not it's a movie
            var isTvShow = guid.Contains("/serie") || guid.Contains("/episodio/") ||
                           Regex.IsMatch(torrentName, @"Cap\.(\s+)?(\d+)", RegexOptions.IgnoreCase);

            string wolfmaxCat;
            if (isTvShow)
            {
                if (quality.Contains("720"))
                {
                    wolfmaxCat = Wolfmax4KCatType.Serie720;
                }
                else if (quality.Contains("1080"))
                {
                    wolfmaxCat = Wolfmax4KCatType.Serie1080;
                }
                else if (quality.ToLower().Contains("4k") || quality.ToLower().Contains("2160p"))
                {
                    wolfmaxCat = Wolfmax4KCatType.Serie4K;
                }
                else
                {
                    wolfmaxCat = Wolfmax4KCatType.Serie;
                }
            }
            else
            {
                if (quality.Contains("720"))
                {
                    wolfmaxCat = Wolfmax4KCatType.Pelicula720;
                }
                else if (quality.Contains("1080"))
                {
                    wolfmaxCat = Wolfmax4KCatType.Pelicula1080;
                }
                else if (quality.ToLower().Contains("4k") || quality.ToLower().Contains("2160p"))
                {
                    wolfmaxCat = Wolfmax4KCatType.Pelicula4K;
                }
                else
                {
                    wolfmaxCat = Wolfmax4KCatType.Pelicula;
                }
            }

            return wolfmaxCat;
        }

        private string ParseQuality(string quality)
        {
            return quality switch
            {
                "4K" => "2160p",
                "4KWebrip" => "WEBRip-2160p",
                _ => quality
            };
        }

        private string ParseSeasonAndEpisode(string episodeText)
        {
            var text = (episodeText ?? "").Trim();
            var match = text switch
            {
                // Episodio 2x01 al 06. / Episodio 4x01 al 4x08.
                _ when Regex.Match(text, @"(\d+)x(\d+) al (?:\d+x)?(\d+)", RegexOptions.IgnoreCase) is { Success: true } range => range,
                // Episodio 3x07 - 3x08. / Episodio 1x05 - 06 - 07 - 08.
                _ when Regex.Match(text, @"(\d+)x(\d+)(?: - (?:\d+x)?(\d+))+\.?$", RegexOptions.IgnoreCase) is { Success: true } list => list,
                // Episodio 1x10 - / Episodio 5x24 - Ahora o nunca.
                _ => Regex.Match(text, @"(\d+)x(\d+)", RegexOptions.IgnoreCase)
            };
            if (!match.Success)
            {
                return "";
            }

            var result = "S" + match.Groups[1].Value.PadLeft(2, '0') + "E" + match.Groups[2].Value.PadLeft(2, '0');
            if (match.Groups[3].Success)
            {
                result += "-E" + match.Groups[3].Value.PadLeft(2, '0');
            }

            return result;
        }

        private static DateTime? ParseDate(string date) =>
            DateTime.TryParse(date?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

        private List<int> GetEpisodesFromTitle(string title)
        {
            var vals = new Regex(@"E(\d+)").Matches(title).Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).ToList();

            if (vals.Count == 1)
            {
                return new List<int> { vals[0] };
            }

            if (vals.Count == 2 && vals[1] > vals[0])
            {
                return Enumerable.Range(vals[0], vals[1] - vals[0] + 1).ToList();
            }

            return new List<int>();
        }
    }

    internal static class Wolfmax4KCatType
    {
        public static string Pelicula => "pelicula";
        public static string Pelicula720 => "pelicula720";
        public static string Pelicula1080 => "pelicula1080";
        public static string Pelicula4K => "pelicula4k";
        public static string Serie => "serie";
        public static string Serie720 => "serie720";
        public static string Serie1080 => "serie1080";
        public static string Serie4K => "serie4k";
    }

    internal sealed record Wolfmax4KItem
    {
        public string Title { get; set; }
        public string DetailsPath { get; set; }
        public string EpisodeText { get; set; }
        public string Quality { get; set; }
        public string Size { get; set; }
        public DateTime? PublishDate { get; set; }
        public string Image { get; set; }
        public string ContentCode { get; set; }
        public string Tabla { get; set; }
    }
}
