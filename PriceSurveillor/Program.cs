using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace PriceSurveillor
{
    internal class Program
    {
        static void Main()
        {
            HttpClient client = new();
            string rawHTML = client.GetAsync("http://www.allkeyshop.com/blog/buy-steam-gift-card-cd-key-compare-prices/").GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string rawOffersJSON = client.GetAsync("http://www.allkeyshop.com/blog/wp-admin/admin-ajax.php?action=get_offers&product=28973&currency=eur&region=&edition=&moreq=&use_beta_offers_display=1").GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();

            #region Fees
            string rawHTMLSearch = "' id='payment-fees.js-js'";
            string rawHTMLBWSearch = "src='";

            var idx1 = rawHTML.IndexOf(rawHTMLSearch);
            var idx2 = rawHTML.LastIndexOf(rawHTMLBWSearch, idx1);
            
            string FeeURL = rawHTML.Substring(idx2 + rawHTMLBWSearch.Length, idx1 - idx2 - rawHTMLBWSearch.Length);

            string rawPrices = client.GetAsync(FeeURL).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult();

            File.WriteAllText(@"node\priceScript.js", $"{rawPrices}\nconsole.log(JSON.stringify(get_payment_fees(), null, 2));");

            Process node = new();
            node.StartInfo.FileName = "node";
            node.StartInfo.Arguments = @"node\priceScript.js";
            node.StartInfo.UseShellExecute = false;
            node.StartInfo.RedirectStandardOutput = true;
            node.StartInfo.CreateNoWindow = true;
            node.Start();

            string rawFeesJSON = string.Empty;

            while (!node.StandardOutput.EndOfStream)
            {
                rawFeesJSON += node.StandardOutput.ReadLine();
            }
            #endregion

            #region JsonParse
            var FeeRoot = JObject.Parse(rawFeesJSON);
            var OfferRoot = JObject.Parse(rawOffersJSON);

            string config = File.ReadAllText(@"config\aks.conf");
            var ConfigRoot = JObject.Parse(config);

            List<string> Currencies = new();
            // Currency, Region
            List<Tuple<string, string>> Regions = new();
            // Currency, Edition, Amount
            List<Tuple<string, string, double>> Editions = new();

            foreach (var x in ConfigRoot)
            {
                Currencies.Add(x.Key);
                foreach (var y in ConfigRoot[x.Key]["regions"])
                {
                    Regions.Add(new(x.Key, (string)y));
                }
                foreach (var y in ConfigRoot[x.Key]["editions"])
                {
                    Editions.Add(new(x.Key, y.Path.Substring(y.Path.LastIndexOf(".") + 1), (double)y));
                }
            }

            foreach (var Currency in Currencies)
            {
                JObject newList;
                List<string> ids = new();

                foreach (dynamic x in OfferRoot["offers"])
                {
                    if (Regions.Where(x => x.Item1 == Currency).Select(x => x.Item2).ToList().Contains((string)x.region))
                    {
                        ids.Add((string)x.id);
                    }
                }

                newList = JObject.FromObject(new
                {
                    offers =
                    from offer in OfferRoot["offers"]
                    where ids.Contains(offer["id"].ToString())
                    select new
                    {
                        id = offer["id"],
                        url = offer["affiliateUrl"],
                        edition = offer["edition"],
                        rawprice = offer["price"]?["eur"]?["priceWithoutCoupon"],
                        coupon = offer["price"]["eur"]["bestCoupon"].HasValues ? offer["price"]?["eur"]?["bestCoupon"]?["code"] : null,
                        couponvalue = offer["price"]["eur"]["bestCoupon"].HasValues ? offer["price"]?["eur"]?["bestCoupon"]?["discountValue"] : null,
                        pricewithcoupon = offer["price"]?["eur"]?["price"],
                        paypalfee = FeeRoot[(string?)offer["merchant"]]?["paypal"]?["9007199254740992"]?["a"],
                        cardfee = FeeRoot[(string?)offer["merchant"]]?["card"]?["9007199254740992"]?["a"],
                        merchant = OfferRoot["merchants"]?[(string?)offer["merchant"]]?["name"]
                    }
                });
                #endregion

                
                #region DataParsing

                // edition | id | price
                List<Tuple<string, string, double>> cheapest = new();

                foreach (dynamic x in newList["offers"])
                {
                    var edition_list = Editions.Where(z => z.Item1 == Currency).Where(z => z.Item2 == (string)x["edition"]).Select(x => x.Item3);
                    bool flag = edition_list.ToArray().Length > 0;

                    if (!flag) continue;

                    double fee = ((x["paypalfee"] != null && x["cardfee"] != null) ? ((double)x["paypalfee"] < (double)x["cardfee"]) : false) ? (double)x["paypalfee"] : (x["cardfee"] != null ? (double)x["cardfee"] : (x["paypalfee"] != null ? (double)x["paypalfee"] : 1));
                    if (fee == 1) continue;

                    double effectiveprrice = Math.Round(edition_list.First() / (double)x["pricewithcoupon"] - edition_list.First() / (double)x["pricewithcoupon"] * (fee - 1), 4);

                    if (!cheapest.Select(x => x.Item1).Contains((string)x["edition"]))
                    {
                        cheapest.Add(new Tuple<string, string, double>((string)x["edition"], (string)x["id"], effectiveprrice));
                    }

                    else if (cheapest.Where(z => z.Item1 == (string)x["edition"]).Select(x => x.Item3).First() < effectiveprrice)
                    {
                        cheapest.Remove(cheapest.Where(z => z.Item1 == (string)x["edition"]).First());
                        cheapest.Add(new Tuple<string, string, double>((string)x["edition"], (string)x["id"], effectiveprrice));
                    }
                }

                cheapest = cheapest.OrderByDescending(x => x.Item3).ToList();
                cheapest.ForEach(x =>
                {
                    var ident = newList["offers"].Where(z => (string)z["id"] == x.Item2);
                    var coupon = ident.Select(z => z["coupon"]).First();
                    var edi = Editions.Where(z => z.Item2 == x.Item1).Select(x => x.Item3).First();

                    string str = (Convert.ToString(coupon).Length > 0) ? ($"{edi}{Currency} | {x.Item3} {Currency}/EUR | {coupon.ToString().Replace("-", "")} | {ident.Select(z => z["merchant"]).First()} | {ident.Select(z => z["url"]).First()}") : ($"{edi}{Currency} | {x.Item3} {Currency}/EUR | {ident.Select(z => z["merchant"]).First()} | {ident.Select(z => z["url"]).First()}");

                    Console.WriteLine(str);
                });
                Console.WriteLine("\n");
            }
            #endregion
            
            Console.Write("\nPress any key to continue...");
            Console.ReadKey();
        }
    }
}