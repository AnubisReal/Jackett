using System;
using System.Threading.Tasks;
using Jackett.Common.Utils;
using Jackett.Common.Indexers.Definitions;
using NUnit.Framework;

namespace Jackett.Test.Common.Utils
{
    [TestFixture]
    public class WolfmaxDownloadTests
    {
        [TestCase("https://enlacito.com/", "ok", 200, "<script>var link_out='AAAA';</script>", true)]
        [TestCase("https://wolftorrent.com/descarga/serie/test", "ok", 200, "link_out", false)]
        [TestCase("https://enlacito.com.attacker.test/", "ok", 200, "link_out", false)]
        [TestCase("http://enlacito.com/", "ok", 200, "link_out", false)]
        [TestCase("https://enlacito.com/", "error", 200, "link_out", false)]
        [TestCase("https://enlacito.com/", "ok", 403, "link_out", false)]
        [TestCase("https://enlacito.com/", "ok", 200, "Completa la verificación", false)]
        public void ByparrMustReachPreparedEnlacitoPage(string url, string status, int http, string html, bool valid)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                status, solution = new { url, status = http, response = html }
            });
            if (valid)
                Assert.That(Wolfmax4K.ParseByparrResponse(json), Is.EqualTo(html));
            else
                Assert.Throws<WolfmaxDownloadException>(() => Wolfmax4K.ParseByparrResponse(json));
        }

        [TestCase("not JSON")]
        [TestCase("{}")]
        [TestCase("{\"status\":\"ok\",\"solution\":null}")]
        public void RejectsInvalidByparrResponse(string json)
        {
            Assert.Throws<WolfmaxDownloadException>(() => Wolfmax4K.ParseByparrResponse(json));
        }

        [TestCase("https://wolftorrent.com/torrents/series/episode.torrent", true)]
        [TestCase("https://wolftorrent.com/torrents/series/episode%20name.torrent", true)]
        [TestCase("http://wolftorrent.com/torrents/episode.torrent", false)]
        [TestCase("https://wolftorrent.com.attacker.test/torrents/episode.torrent", false)]
        [TestCase("https://attacker.test/torrents/episode.torrent", false)]
        [TestCase("https://user:password@wolftorrent.com/torrents/episode.torrent", false)]
        [TestCase("https://wolftorrent.com:444/torrents/episode.torrent", false)]
        [TestCase("https://wolftorrent.com/torrents/../cuenta.torrent", false)]
        [TestCase("https://wolftorrent.com/api/descargas", false)]
        [TestCase("https://wolftorrent.com/torrents/episode.torrent?redirect=elsewhere", false)]
        [TestCase("https://wolftorrent.com/torrents/episode.torrent#fragment", false)]
        [TestCase("file:///tmp/episode.torrent", false)]
        [TestCase("javascript:alert(1)", false)]
        [TestCase("not a URL", false)]
        [TestCase(null, false)]
        public void PreparedDestinationMustStayOnTorrentOrigin(string destination, bool expected)
        {
            Assert.That(Wolfmax4K.IsAllowedTorrentDestination(destination, new Uri("https://wolftorrent.com/"), out _), Is.EqualTo(expected));
        }

        [TestCase("VTJGc2RHVmtYMS9rS2plMDBUNTZObUd5bHREOHBUQ0RHdUxRaWdNZllmcVZLUGtXdCtqaVFwbk1za3JsakNrSktkUGtEY0d0WXFrZW5UdG5hamtERTdLdTZoNm8xaW9hYk90eUYwalpWVjFmTng0UFJidklDL3k3a2VTb01RcVBidTIxSnUySUNOak93U3ZoVFZrN25RNkZNOC9YK2tza09KZGNBSmhMVW1xVWREblZLWGZlcjIxNGFSQzRJbmI2", "4x02")]
        [TestCase("VTJGc2RHVmtYMStwdDNrUE1lWTRvUkhxNVd6QnVWZEptUGwxYjhnL2JVNjYyR1pOYjN0d1JIUTRiOC90amVWZFFBTkpKRU81b3BraFNtaGx3dE5peVk5UGp2Zmhua0FLUmthQk5IOU85eThXM05FTlRUcU1uTGVpNXM0SGZ5ZUwxMzlGQ0VDblU0UkorY2RxZzhhRDN5NGwxOXBrVTRFd0ppUGJ3dVgzaUFpSW5GMlJIQ216RjZncnJnWFI2NFVM", "4x01")]
        public async Task DecodesObservedEnlacitoPages(string encoded, string episode)
        {
            var html = "<script>var link_out = \"" + encoded + "\";</script>";
            var target = await Wolfmax4K.ResolvePreparedTorrentAsync(html, new Uri("https://wolftorrent.com/"));
            Assert.That(target.AbsolutePath, Does.Contain(episode));
            Assert.That(target.Host, Is.EqualTo("wolftorrent.com"));
        }

        [TestCase("<html>Cloudflare challenge</html>")]
        [TestCase("<script>var link_out = 'AAAA';</script>")]
        [TestCase("<script>var link_out = 'QUFBQQ==';</script>")]
        public void RejectsMissingOrCorruptPreparedLink(string html)
        {
            Assert.ThrowsAsync<WolfmaxDownloadException>(async () =>
                await Wolfmax4K.ResolvePreparedTorrentAsync(html, new Uri("https://wolftorrent.com/")));
        }
    }
}
