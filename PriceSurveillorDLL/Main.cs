using System.Diagnostics;
using System.Drawing;
using Newtonsoft.Json.Linq;
using OfficeOpenXml;


namespace PriceSurveillorDLL
{
    public static class PriceSurveillor
    {
        public static void Start()
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
            node.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
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

            if (File.Exists("data.xlsx")) File.Delete("data.xlsx");
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using (var package = new ExcelPackage(new FileInfo("data.xlsx")))
            {
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
                            coupon = offer["price"]["eur"]["bestCoupon"].HasValues ? offer["price"]?["eur"]?["bestCoupon"]?["code"] : "N/A",
                            couponvalue = offer["price"]["eur"]["bestCoupon"].HasValues ? offer["price"]?["eur"]?["bestCoupon"]?["discountValue"] : 0.0,
                            pricewithcoupon = offer["price"]?["eur"]?["price"],
                            paypalfee = FeeRoot[(string?)offer["merchant"]]?["paypal"]?["9007199254740992"]?["a"],
                            cardfee = FeeRoot[(string?)offer["merchant"]]?["card"]?["9007199254740992"]?["a"],
                            merchant = OfferRoot["merchants"]?[(string?)offer["merchant"]]?["name"]
                        }
                    });
                    #endregion


                    #region DataParsing

                    // store, [ edition, id, price, amount ]
                    List<Tuple<string, List<Tuple<string, string, double, double>>>> StoreCheapest = new();

                    foreach (dynamic x in newList["offers"])
                    {
                        var edition_list = Editions.Where(z => z.Item1 == Currency).Where(z => z.Item2 == (string)x["edition"]).Select(x => x.Item3);
                        bool flag = edition_list.ToArray().Any();
                        if (!flag) continue;
                        double amount = edition_list.First();

                        double fee = ((x["paypalfee"] != null && x["cardfee"] != null) ? ((double)x["paypalfee"] < (double)x["cardfee"]) : false) ? (double)x["paypalfee"] : (x["cardfee"] != null ? (double)x["cardfee"] : (x["paypalfee"] != null ? (double)x["paypalfee"] : 1));
                        if (fee == 1) continue;

                        double effectiveprrice = Math.Round(amount / (double)x["pricewithcoupon"] - amount / (double)x["pricewithcoupon"] * (fee - 1), 4);

                        // Store Cheapest
                        if (!StoreCheapest.Where(z => z.Item1 == (string)x["merchant"]).Select(x => x).Any())
                        {
                            StoreCheapest.Add(new((string)x["merchant"], new()));
                            StoreCheapest.Where(z => z.Item1 == (string)x["merchant"]).Select(x => x.Item2).First().Add(new((string)x["edition"], (string)x["id"], effectiveprrice, amount));
                        }
                        else if (!StoreCheapest.Where(z => z.Item1 == (string)x["merchant"]).Select(x => x.Item2).First().Where(z => z.Item1 == (string)x["edition"]).Select(x => x).Any())
                        {
                            StoreCheapest.Where(z => z.Item1 == (string)x["merchant"]).Select(x => x.Item2).First().Add(new((string)x["edition"], (string)x["id"], effectiveprrice, amount));
                        }
                        else if (StoreCheapest.Where(z => z.Item1 == (string)x["merchant"]).Select(x => x.Item2).First().Where(z => z.Item1 == (string)x["edition"]).Select(x => x.Item3).First() < effectiveprrice)
                        {
                            StoreCheapest.Remove(StoreCheapest.Where(z => z.Item1 == (string)x["edition"] && z.Item2.Where(z => z.Item1 == (string)x["edition"]).Select(x => x.Item1).First() == (string)x["edition"]).First());

                            if (!StoreCheapest.Where(z => z.Item1 == (string)x["merchant"]).Select(x => x).Any())
                                StoreCheapest.Add(new((string)x["merchant"], new()));
                            StoreCheapest.Where(z => z.Item1 == (string)x["merchant"]).Select(x => x.Item2).First().Add(new((string)x["edition"], (string)x["id"], effectiveprrice, amount));
                        }
                    }

                    #region Excel

                    var ws = package.Workbook.Worksheets.Add($"{Currency}/EUR");
                    ws.Cells["A1"].Value = "Stores/Amount";

                    char col = 'B';
                    int i = 3;

                    List<string> edis = new(); StoreCheapest.ForEach(x => edis.AddRange(x.Item2.Select(x => x.Item1)));

                    Editions.Where(z => z.Item1 == Currency).Where(z => edis.Contains(z.Item2)).Select(x => x.Item3).ToList().ForEach(z =>
                    {
                        ws.Cells[$"{col}1"].Value = z;
                        ws.Cells[$"{col}1"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.CenterContinuous;
                        col++;
                    });

                    col++; col++;
                    ws.Cells[$"{col}1"].Value = "Coupon Code";
                    ws.Cells[$"{col}1"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.CenterContinuous;
                    char couponcol = col;
                    col++;
                    char couponcol2 = col;
                    ws.Cells[$"{col}1"].Value = "Coupon Effect";
                    ws.Cells[$"{col}1"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.CenterContinuous;
                    col = 'B';

                    Tuple<double, string> cheapestMerchant = new(0, "");

                    StoreCheapest.ForEach(x =>
                    {
                        IEnumerable<JToken> ident = default;
                        double amount = 0.0;
                        foreach (var y in newList["offers"])
                        {

                            var id2 = x.Item2.Where(z => z.Item2 == (string)y["id"]).Select(x => x.Item2);
                            if (id2.Any())
                                ident = newList["offers"].Where(z => (string)z["id"] == id2.First());
                            else
                                continue;
                            amount = x.Item2.Where(z => z.Item2 == (string)y["id"]).Select(x => x.Item4).First();
                        }
                        string merchant = ident.Select(z => z["merchant"]).First().ToString();

                        ws.Cells[$"A{i}"].Value = merchant;

                        // price, amount, url, coupon, couponval
                        List<Tuple<double, double, string, string, double>> OfferList = new();

                        foreach (var y in x.Item2)
                        {
                            var thingie = newList["offers"].Where(z => (string)z["id"] == y.Item2);
                            string url = thingie.Select(x => (string)x["url"]).First();
                            string coupon = thingie.Select(x => (string)x["coupon"]).First();
                            double couponval = thingie.Select(x => (double)x["couponvalue"]).First();
                            OfferList.Add(new(y.Item3, y.Item4, url, coupon, couponval));
                        }

                        foreach (var z in OfferList)
                        {
                            if (cheapestMerchant.Item1 < z.Item1)
                                cheapestMerchant = new(z.Item1, merchant);
                            for (char j = 'B'; true; j++)
                            {
                                if (ws.Cells[$"{j}1"].Value.ToString() == z.Item2.ToString())
                                {
                                    var cell = ws.Cells[$"{j}{i}"];
                                    cell.Value = z.Item1;
                                    cell.Style.Font.UnderLine = true;
                                    cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.None;
                                    cell.Style.Font.Color.SetColor(Color.Blue);
                                    cell.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.CenterContinuous;
                                    cell.Hyperlink = new(z.Item3);
                                    break;
                                }
                                ws.Cells[$"{couponcol}{i}"].Value = z.Item4;
                                ws.Cells[$"{couponcol}{i}"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.CenterContinuous;
                                ws.Cells[$"{couponcol2}{i}"].Value = $"-{z.Item5}%";
                                ws.Cells[$"{couponcol2}{i}"].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.None;
                                ws.Cells[$"{couponcol2}{i}"].Style.Font.Color.SetColor(Color.Green);
                                ws.Cells[$"{couponcol2}{i}"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.CenterContinuous;
                            }

                        }

                        OfferList = OfferList.OrderByDescending(z => z.Item1).ToList();
                        for (char j = 'B'; true; j++)
                        {
                            if (ws.Cells[$"{j}1"].Value.ToString() == OfferList.First().Item2.ToString())
                            {
                                var cell = ws.Cells[$"{j}{i}"];
                                cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                                cell.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                                break;
                            }
                        }

                        col++;
                        i++;
                    });

                    for (int j = 3; j < i; j++)
                    {
                        if (ws.Cells[$"A{j}"].Value.ToString() == cheapestMerchant.Item2.ToString())
                        {
                            ws.Cells[$"A{j}"].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                            ws.Cells[$"A{j}"].Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                            break;
                        }
                    }
                    ws.Cells[1, 1, i, couponcol2].AutoFitColumns();
                    //ws.Cells[1, 1, i, 1].AutoFitColumns();

                    #endregion
                }
                package.Save();
            }
            #endregion

            var excel = new Process();
            excel.StartInfo.FileName = ".\\data.xlsx";
            excel.StartInfo.UseShellExecute = true;
            try
            {
                excel.Start();
            }
            catch { }

        }
    }
}