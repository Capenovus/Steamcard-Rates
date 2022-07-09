using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace PriceSurveillor
{
    internal class Program
    {
        static void Main(string[] args)
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


            string[] allowedEditions = { "448", "995", "1107", "1129", "1344", "1586" , "1634" , "2005", "tl50", "tl100"};

            JObject newList;
            string[] ids = { };
            string[] merchants;


            foreach (dynamic x in OfferRoot["offers"])
            {
                if (allowedEditions.ToList().Contains((string)x.edition))
                {
                    ids += x.id;
                }

            }

            newList = JObject.FromObject(new
            {
                offers = 
                from offer in OfferRoot["offers"]
                where offer["id"] in ids
                select new
                {
                    merchant = OfferRoot["merchants"][offer["merchant"]]["name"]
                }

            });

            Console.WriteLine(newList);

            foreach (var y in FeeRoot)
            {
                // Do stuffs
            }

            #endregion

            Console.Read();
        }
    }
}