using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net.Mail;
using Newtonsoft.Json;
using System.Net;
using Sentry;

namespace PPDRouteDeviatedVehicles
{
    internal class Program
    {
        private static string connectionStringGPSTrack;
        private static string connectionStringBTS;
        private static List<string> ErrorsList = new List<string>();
        private static DateTime endTime = DateTime.Now;
        private static DateTime startTime = DateTime.Now;

        private static async Task<string> GetSentryDsn()
        {
            try
            {
                using var client = new HttpClient();
                string encryptedDsn = await client.GetStringAsync("https://external.balajitransports.in/api/Sentry/GetDsn?type=consoleapplications");
                return AesDecryptionHelper.Decrypt(encryptedDsn.Trim().Trim('"'));
            }
            catch (Exception ex)
            {
                ErrorsList.Add($"Failed to fetch Sentry DSN: \n{ex.Message}\n{ex.StackTrace}");
                Console.WriteLine($"Failed to fetch Sentry DSN: {ex.Message}");
                return string.Empty; 
            }
        }

        static async Task Main(string[] args)
        {
#if DEBUG
            string sentryDsn = string.Empty;
#else
            string sentryDsn = await GetSentryDsn();
#endif

            using (SentrySdk.Init(s =>
            {
                s.Dsn = sentryDsn;
                s.Debug = false;
                s.TracesSampleRate = 1.0;
                s.Environment = "PPDRouteDeviatedVehicles";
            }))
            {
                await RunApplicationLogic();

                if (ErrorsList.Count > 0)
                {
                    Console.WriteLine("Errors:");

                    ErrorsList.ForEach(e => Console.WriteLine(e));

                    string allErrors = string.Join(Environment.NewLine, ErrorsList);

                    await LogMonitor(
                        startTime,
                        endTime,
                        allErrors,
                        "PPDRouteDeviatedVehicles"
                    );
                }
                else
                {
                    await LogMonitor(
                        startTime,
                        endTime,
                        "",
                        "PPDRouteDeviatedVehicles"
                    );
                }

                Console.WriteLine("Process completed.");
            }
        }

        #region AES Helper
        public static class AesDecryptionHelper
        {
            private static readonly string key = "NLKpQsoPaeoZ55ul";
            private static readonly string iv = "RbeqxtNXxucHI123";

            public static string Decrypt(string cipherText)
            {
                using Aes aesAlg = Aes.Create();
                aesAlg.Key = Encoding.UTF8.GetBytes(key);
                aesAlg.IV = Encoding.UTF8.GetBytes(iv);

                var decryptor = aesAlg.CreateDecryptor();
                using var msDecrypt = new MemoryStream(Convert.FromBase64String(cipherText));
                using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
                using var srDecrypt = new StreamReader(csDecrypt);

                return srDecrypt.ReadToEnd();
            }
        }
        #endregion

        private static async Task RunApplicationLogic()
        {
            try
            {
                #region Get Connection Strings
                using var client = new HttpClient();

                string encryptedBTS =
                    await client.GetStringAsync("https://external.balajitransports.in/api/DBConnectionString/BTS");

                string encryptedGPSTracking =
                    await client.GetStringAsync("https://external.balajitransports.in/api/DBConnectionString/GPSTracking");

                connectionStringBTS = AesDecryptionHelper.Decrypt(encryptedBTS);
                connectionStringGPSTrack = AesDecryptionHelper.Decrypt(encryptedGPSTracking);
                #endregion

                #region Fetch Params
                DataTable rakeParamsTable = new DataTable();

                using (SqlConnection con = new SqlConnection(connectionStringBTS))
                using (SqlCommand cmd = new SqlCommand("GetPPDRouteDeviatedParams", con))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    using SqlDataAdapter da = new SqlDataAdapter(cmd);
                    da.Fill(rakeParamsTable);
                }
                #endregion

                #region Loop & Call Deviation SP
                using (SqlConnection con = new SqlConnection(connectionStringGPSTrack))
                {
                    con.Open();

                    foreach (DataRow row in rakeParamsTable.Rows)
                    {
                        try
                        {
                            int routeId = Convert.ToInt32(row["PPDRouteId"]);
                            string vehicleId = row["IntangleId"].ToString();
                            DateTime fromDate = Convert.ToDateTime(row["FromDate"]);
                            DateTime toDate = Convert.ToDateTime(row["ToDate"]);
                            int shipmentId = Convert.ToInt32(row["ShipmentId"]);
                            bool isShipmentDeviated = false;

                            using SqlCommand cmd = new SqlCommand("ReportRouteDeviationForVehiclePPD", con);
                            cmd.CommandType = CommandType.StoredProcedure;

                            cmd.Parameters.Add("@RouteId", SqlDbType.Int).Value = routeId;
                            cmd.Parameters.Add("@VehicleId", SqlDbType.VarChar, 50).Value = vehicleId;
                            cmd.Parameters.Add("@FromDate", SqlDbType.DateTime2).Value = fromDate;
                            cmd.Parameters.Add("@ToDate", SqlDbType.DateTime2).Value = toDate;
                            cmd.Parameters.Add("@CorridorWidthMeters", SqlDbType.Float).Value = 250.0;
                            using SqlDataReader reader = cmd.ExecuteReader();
                            //while (reader.Read())
                            //{
                            //    Console.WriteLine(
                            //        $"Route:{routeId} | " +
                            //        $"Vehicle:{reader["vehicle_id"]} | " +
                            //        $"Time:{reader["dt"]} | " +
                            //        $"Lat:{reader["Lat"]}, Lng:{reader["Lng"]} | " +
                            //        $"Distance:{reader["DistanceFromRouteMeters"]}m | " +
                            //        $"Deviation:{reader["IsRouteDeviation"]}"
                            //    );
                            //}
                            while (reader.Read())
                            {
                                bool isDeviated = Convert.ToBoolean(reader["IsRouteDeviation"]);

                                if (isDeviated)
                                {
                                    isShipmentDeviated = true;
                                    break; // no need to check further
                                }
                            }
                            reader.Close();
                            if (isShipmentDeviated)
                            {
                                int rows = UpdateShipmentAsDeviated(shipmentId);

                                if (rows > 0)
                                    Console.WriteLine($"✅ Shipment {shipmentId} updated successfully.");
                                else
                                    Console.WriteLine($"⚠ Shipment {shipmentId} already deviated.");
                            }
                            else
                            {
                                Console.WriteLine($"❌ Shipment {shipmentId} not deviated.");
                            }
                        }


                        catch (Exception rowEx)
                        {
                            ErrorsList.Add(
                                $"Row Error | Route:{row["PPDRouteId"]}, Vehicle:{row["IntangleId"]} | {rowEx.Message}"
                            );
                        }
                    }
                }
                #endregion
            }
            catch (Exception ex)
            {
                ErrorsList.Add(
                    $"Fatal Error in RunApplicationLogic\n{ex.Message}\n{ex.StackTrace}"
                );
            }
        }

        private static int UpdateShipmentAsDeviated(int shipmentId)
        {
            using SqlConnection con = new SqlConnection(connectionStringBTS);
            
            con.Open();

            using SqlCommand cmd = new SqlCommand("InsertRouteDeviationForPPD", con);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.Add("@ShipmentId", SqlDbType.Int).Value = shipmentId;

            return cmd.ExecuteNonQuery();
        }


        private static async Task LogMonitor(DateTime startTime, DateTime endTime, string error, string appName)
        {

            try
            {


                using (SqlConnection conn = new SqlConnection(connectionStringBTS))
                {
                    await conn.OpenAsync();
                    using (SqlCommand cmd = new SqlCommand("InsertExtensionMonitor", conn))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.AddWithValue("@Timestamp", endTime);
                        cmd.Parameters.AddWithValue("@StartTime", startTime);
                        cmd.Parameters.AddWithValue("@ErrorName", (object)error ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@Extensionname", appName);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                Console.WriteLine("Logged execution details.");
            }
            catch (Exception logEx)
            {
                Console.WriteLine($"Logging to ExtensionMonitor failed: {logEx.Message}");
            }
        }


        static void SendWhatsappMessage(string token, string message, string chatId)
        {
            var url = "https://app.whatsera.com/api/send/text"; // WhatsApp API URL

            var httpClient = new HttpClient();

            // Create the JSON payload
            var payload = new
            {
                accessToken = token,
                mobile = chatId,
                text = message

            };

            var jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                // Send the POST request synchronously
                var response = httpClient.PostAsync(url, content).Result;

                // Read and return the response synchronously
                var responseString = response.Content.ReadAsStringAsync().Result;
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Message sent successfully!");
                    Console.WriteLine(responseString); // Display the response
                }
                else
                {
                    Console.WriteLine($"Error: {response.StatusCode} - {responseString}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred: {ex.Message}");
            }
        }


        static async Task SendWhatsAppMessageAsync(List<string> phoneNumbers, string individualmsg)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(15);

                var payload = new
                {
                    phoneNumber = phoneNumbers.ToArray(),
                    message = individualmsg
                };

                //Console.WriteLine($"{client.BaseAddress}:{payload}");
                string jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                try
                {
                    HttpResponseMessage response = await client.PostAsync("https://external.balajitransports.in/api/WhatseraAPI/SendWhatsAppMessage", content);

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Message API call succeeded (HTTP 200).");
                        string responseBody = await response.Content.ReadAsStringAsync();
                        Console.WriteLine("Response Body:\n" + responseBody);
                    }

                    else
                    {
                        string responseContent = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"Error sending message: {response.StatusCode}, Response: {responseContent}");
                        DateTime toDate = DateTime.Now;

                        //DateTime toDate = DateTime.ParseExact("2025-04-11 14:00", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

                        DateTime fromDate = toDate.AddHours(-2);


                    }
                }
                catch (Exception ex)
                {

                    Console.WriteLine("An error occurred: " + ex.Message);
                }
            }
        }



        private static async Task SendMailWithoutAttachment(string subject, string body)
        {
            var basicCredential = new NetworkCredential("ppdbalaji@gmail.com", "uxrsfstcesqhkjca");
            var fromAddress = new MailAddress("ppdbalaji@gmail.com", "FastTagWhatsappAlert");
            var toAddress = new MailAddress("rekharekha14270@gmail.com", "Rekha T R");
            // var ccAddress = new MailAddress("mkichidi@gmail.com", "G Monoj Kichidi");

            using (var smtp = new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = basicCredential
            })
            {
                var message = new MailMessage(fromAddress, toAddress)
                {
                    Subject = subject,
                    Body = body
                };
                message.CC.Add(toAddress);

                await smtp.SendMailAsync(message);
            }
        }
    }
}
