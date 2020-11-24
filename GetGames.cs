using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Graph;
using Newtonsoft.Json;

namespace azfx_calendars_functions_dotnet
{
    public static class GetGames
    {
        public static readonly HttpClient client = new HttpClient();

        [Disable("IS_DISABLED")]
        [FunctionName("GetGames")]
        public static async Task Run([TimerTrigger("0 0 12 * * *")]TimerInfo myTimer, ILogger log)
        {
            try 
            {
                HttpResponseMessage teamsResponse = await client.GetAsync("https://statsapi.mlb.com/api/v1/teams?sportId=1");
                string teamsResponseBody = await teamsResponse.Content.ReadAsStringAsync();
                Teams.Root teams = JsonConvert.DeserializeObject<Teams.Root>(teamsResponseBody);
                string[] scopes = new string[] { "https://graph.microsoft.com/.default" };

                string url = String.Format("https://login.microsoftonline.com/{0}/oauth2/v2.0/token", Environment.GetEnvironmentVariable("TENANT_ID"));
                IConfidentialClientApplication confidentialClientApplication = ConfidentialClientApplicationBuilder.Create(Environment.GetEnvironmentVariable("CLIENT_ID")).WithClientSecret(Environment.GetEnvironmentVariable("CLIENT_SECRET")).WithAuthority(url).Build();
                GraphServiceClient graphServiceClient = new GraphServiceClient(new DelegateAuthenticationProvider(async (HttpRequestMessage requestMessage) => {
                    AuthenticationResult authenticationResult = await confidentialClientApplication.AcquireTokenForClient(scopes).ExecuteAsync();
                    requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authenticationResult.AccessToken);
                }));
                
                foreach (azfx_calendars_functions_dotnet.Teams.Team team in teams.teams)
                {
                    string teamId = team.id.ToString();
                    string teamName = team.name;
                    string requestUri = string.Format("https://statsapi.mlb.com/api/v1/schedule?sportId=1&teamId={0}&startDate={1}&endDate={2}", teamId, Environment.GetEnvironmentVariable("START_DATE"), Environment.GetEnvironmentVariable("END_DATE"));
                    HttpResponseMessage response = await client.GetAsync(requestUri);
                    string responseBody = await response.Content.ReadAsStringAsync();
                    Schedule.Root schedule = JsonConvert.DeserializeObject<Schedule.Root>(responseBody);

                    string siteName = team.name.Replace(" ", String.Empty);
                    string serverRelativeUrl = string.Format("/sites/{0}", siteName);

                    foreach (Schedule.Date date in schedule.dates)
                    {
                        foreach (Schedule.Game game in date.games)
                        {
                            string opponent = game.teams.home.team.id == team.id ? game.teams.away.team.name : game.teams.home.team.name;
                            Boolean isHomeTeam = game.teams.home.team.id == team.id ? true : false;
                            Dictionary<string, object> additionalData = new Dictionary<string, object>();
                            List<string> gameType = new List<string>();
                            string gamePk = game.gamePk.ToString();
                            string filter = string.Format("fields/GamePK eq {0}", gamePk);
                            List<QueryOption> queryOptions = new List<QueryOption>()
                            {
                                new QueryOption("expand", "fields(select=GamePK,Title,Location,GameType,GameStatus,StartDate,EndDate,HomeTeamScore,AwayTeamScore,GamePK)"),
                                new QueryOption("filter", filter)
                            };
                            IListItemsCollectionPage listItems = await graphServiceClient.Sites["noahspan.sharepoint.com"].SiteWithPath(serverRelativeUrl).Lists["Games"].Items.Request(queryOptions).GetAsync();
                            
                            switch(game.gameType)
                            {
                                case "E":
                                    gameType.Add("Exhibition");
                                    break;
                                case "S":
                                    gameType.Add("Spring Training");
                                    break;
                                case "R":
                                    gameType.Add("Regular Season");
                                    break;
                                case "D":
                                    gameType.Add("Division");
                                    break;
                                case "L":
                                    gameType.Add("League Championship Series");
                                    break;
                                case "W":
                                    gameType.Add("World Series");
                                    break;
                                default:
                                    break;
                            }

                            additionalData.Add("Title", opponent);
                            additionalData.Add("IsHomeTeam", isHomeTeam);
                            additionalData.Add("Location", game.venue.name);
                            additionalData.Add("GameType", gameType[0].ToString());
                            additionalData.Add("GameStatus", game.status.detailedState);
                            additionalData.Add("StartDate", game.gameDate);
                            additionalData.Add("EndDate", Convert.ToDateTime(game.gameDate).ToUniversalTime().AddHours(3).ToString("yyyy-MM-ddTHH:mm:ssZ"));
                            additionalData.Add("GamePK", gamePk);

                            if (game.teams.home.score != null) 
                            {
                                additionalData.Add("HomeTeamScore", game.teams.home.score);
                            }

                            if (game.teams.away.score != null) 
                            {
                                additionalData.Add("AwayTeamScore", game.teams.away.score);
                            }

                            if (game.rescheduleDate != null) 
                            {
                                additionalData.Add("RescheduleDate", game.rescheduleDate);
                            }

                            if (game.rescheduledFrom != null) 
                            {
                                additionalData.Add("RescheduledFrom", game.rescheduledFrom);
                            }

                            if (listItems.Count > 0) {
                                foreach (ListItem listItem in listItems)
                                {
                                    IDictionary<string, object> listItemFields = listItem.Fields.AdditionalData;
                                    Dictionary<string, object> fieldValuesToUpdate = new Dictionary<string, object>();
                                    
                                    foreach (KeyValuePair<string, object> entry in listItemFields) {
                                        if (entry.Key != "@odata.etag") {
                                            var listItemFieldValue = entry.Value.ToString();
                                            var additionalDataKeyValue = additionalData[entry.Key].ToString();
                                            
                                            if (listItemFieldValue != additionalDataKeyValue) {
                                                fieldValuesToUpdate.Add(entry.Key, additionalDataKeyValue);
                                            }
                                        }
                                    }

                                    if (fieldValuesToUpdate.Count > 0) 
                                    {
                                        FieldValueSet fieldValueSet = new FieldValueSet
                                        {
                                            AdditionalData = fieldValuesToUpdate
                                        };

                                        await graphServiceClient.Sites["noahspan.sharepoint.com"].SiteWithPath(serverRelativeUrl).Lists["Games"].Items[listItem.Id].Fields.Request().UpdateAsync(fieldValueSet);
                                        log.LogInformation(string.Format("{0} game {1} was updated", teamName, gamePk));
                                    }
                                }
                            } else {
                                ListItem listItem = new ListItem
                                {
                                    Fields = new FieldValueSet
                                    {
                                        AdditionalData = additionalData
                                    }
                                };

                                await graphServiceClient.Sites["noahspan.sharepoint.com"].SiteWithPath(serverRelativeUrl).Lists["Games"].Items.Request().AddAsync(listItem);
                                log.LogInformation(string.Format("{0} game {1} was added", teamName, gamePk));
                            }
                        }
                    }
                }
            }
            catch (HttpRequestException e)
            {
                log.LogInformation("Error: " + e.Message);
            }
            finally
            {
                log.LogInformation("Timer job completed");
            }
        }
    }
}
