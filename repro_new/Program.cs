using System;
using System.Net;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LetterboxdRepro
{
    class Program
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private const string Url = "https://letterboxd.com/user/login.do";

        static async Task Main(string[] args)
        {
            Console.WriteLine($"Testing connection to {Url}...");

            await Test("Test 1: No Headers", null);
            await Test("Test 2: With User-Agent Only", new Dictionary<string, string> { { "User-Agent", UserAgent } });
            // Mimic full headers from the second request in Authenticate
            await Test("Test 3: Full Headers (Mimicking Authenticate 2nd request)", new Dictionary<string, string> { 
                { "User-Agent", UserAgent },
                { "DNT", "1" },
                { "Host", "letterboxd.com" },
                { "Origin", "https://letterboxd.com" },
                { "Referer", "https://letterboxd.com/" },
                { "Sec-Fetch-Dest", "empty" },
                { "Sec-Fetch-Mode", "cors" },
                { "Sec-Fetch-Site", "same-origin" },
                { "TE", "trailers" }
            });
        }

        static async Task Test(string name, Dictionary<string, string> headers)
        {
            Console.WriteLine($"\n--- {name} ---");
            try
            {
                var handler = new HttpClientHandler { UseCookies = true };
                using (var client = new HttpClient(handler))
                {
                    if (headers != null)
                    {
                        foreach (var kvp in headers)
                        {
                            client.DefaultRequestHeaders.Add(kvp.Key, kvp.Value);
                        }
                    }

                    // Simulate the empty POST that Authenticate does first
                    var response = await client.PostAsync(Url, new FormUrlEncodedContent(new Dictionary<string, string> { }));
                    Console.WriteLine($"Status: {response.StatusCode} ({(int)response.StatusCode})");
                    
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Console.WriteLine("Response headers:");
                        foreach(var h in response.Headers)
                        {
                            Console.WriteLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                        }
                    }
                    else 
                    {
                         Console.WriteLine("Success (200 OK)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
            }
        }
    }
}
