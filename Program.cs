using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using static System.Net.Mime.MediaTypeNames;

namespace TelePressure
{
    public class Settings
    {
        public Users users { get; set; }


        public Dictionary<string, PressureCollection> jsonPressure { get; set; }

        [JsonIgnore]
        public Dictionary<long,PressureCollection> pressure {get; set; }

        public string id { get; set; }

        [JsonIgnore]
        public bool dirty { get; set; }
    }
    public class Users
    {
        public Users()
        {
            this.registeredUsers = new HashSet<long>();
        }

        public HashSet<long> registeredUsers { get; set; }
    }

    public class SimpleTime
    {
        public SimpleTime(int hour, int minute, int second)
        {
            Hour = hour;
            Minute = minute;
            Second = second;
        }

        public int Hour { get; set; }
        public int Minute { get; set; }
        public int Second { get; set; }
    }
 
    public class PressureCollection
    {
        public PressureCollection()
        {
            this.pressures = new List<string>();
            this.startTime = new SimpleTime(8, 0, 0);
            this.endTime = new SimpleTime(20, 0, 0);
        }

        public List<string> pressures  { get; set; }
        public SimpleTime startTime { get; set; }
        public SimpleTime endTime { get; set; }

        
    }
    class Program
    {
        private const string SettingsPath = "./settings.json";

        static Settings settings;
        static TelegramBotClient bot;
        static System.Timers.Timer aTimer;
        static void Main(string[] args)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            /*
            AppDomain.CurrentDomain.ProcessExit += (s, e) => cancellationTokenSource.Cancel();
            Console.CancelKeyPress += (s, e) => cancellationTokenSource.Cancel();
            */
            
            
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                if (!cancellationTokenSource.IsCancellationRequested) {
                    cancellationTokenSource.Cancel();
                    OnApplicationExit();
                }
            };
            
            Console.CancelKeyPress += (sender, e) =>
            {
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                    OnApplicationExit();
                }
            };

            if (File.Exists(SettingsPath))
            {
                //Load settings
                var jsonUtf8Bytes = System.IO.File.ReadAllBytes(SettingsPath);
                var readOnlySpan = new ReadOnlySpan<byte>(jsonUtf8Bytes);
                settings = JsonSerializer.Deserialize<Settings>(readOnlySpan);
                settings.pressure = settings.jsonPressure.ToDictionary(x => long.Parse(x.Key), x => x.Value);
                settings.dirty = false;
            }
            else
            {
                settings = new Settings();
                settings.id = "ENTER ID HERE";
                settings.pressure = new Dictionary<long, PressureCollection>();
                settings.users = new Users();

                settings.dirty = true;
                SaveSettings();
                return;
            }

            //Timer Values
            var minute = 60 * 1000;
            var timerValue = 20 * minute;

            var timeNow = DateTime.Now;
            var timeTillNextHour = 60-timeNow.Minute;
            //Setup Timer
            var nextTime = timeTillNextHour * minute;
            Console.WriteLine("NEXT TIME " + timeTillNextHour);
            aTimer = new System.Timers.Timer(nextTime);

            //Setup Timer so it does it in 1 hour intervals
            void AdjustTimeTo1Hour(object sender, ElapsedEventArgs args)
            {
                aTimer.Interval = timerValue;
                aTimer.Elapsed -= AdjustTimeTo1Hour;
            }

            aTimer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            aTimer.Elapsed += AdjustTimeTo1Hour;
            aTimer.Start();

            //Make bot
            bot = new TelegramBotClient(settings.id);
            bot.OnMessage += BotOnMessage;

            

            Task task = RunBot(cancellationTokenSource);
            task.Wait();

            bot.StopReceiving();
            
            aTimer.Stop();
            
            Console.WriteLine("DONE");
        }

        private static void OnApplicationExit()
        {
            Console.WriteLine("Shutting down");
            SaveSettings();
            Console.WriteLine("All done");
        }

        private static async Task RunBot(CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                bot.StartReceiving();
                Console.WriteLine("Running");

                /*
                // Only in interactive mode
                while (true)
                {
                    var s = Console.ReadLine();
                    if ("exit" == s) break;
                     
                }
                */
                await Task.Delay(-1, cancellationTokenSource.Token).ContinueWith(t => { });
            }
            catch (ApiRequestException e)
            {
                Console.Error.WriteLine(e);
            }
        }
        private static object settingsLock = new object();
        private static void SaveSettings()
        {
            lock (settingsLock)
            {
                if (settings.dirty)
                {
                    settings.dirty = false;
                    settings.jsonPressure = settings.pressure.ToDictionary(x => x.Key.ToString(), x => x.Value);
                    Console.WriteLine("Saving changes");
                    var jsonOptions = new JsonSerializerOptions()
                    {
                        WriteIndented = true
                    };
                    var jsonString = JsonSerializer.SerializeToUtf8Bytes(settings, jsonOptions);
                    try
                    {
                        File.WriteAllBytes(SettingsPath, jsonString);
                        Thread.Sleep(1000);
                    }
                    catch(Exception e)
                    {
                        Console.Error.WriteLine(e);
                    }
                }
            }
        }

        static void SendPressure(long u)
        {
            var s = new StringBuilder();
            int i = 0;

            if(settings.pressure.TryGetValue(u, out var press)) { 
                foreach (var p in press.pressures )
                {
                    string n = (++i).ToString().PadLeft(3,'0');
                    s.Append(n);
                    s.Append(" -=- ");
                    s.Append(p);
                    s.Append("\n");
                }

                bot.SendTextMessageAsync(
                  chatId: u,
                  text: s.ToString()
                );
            }
        }
        private static void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            var time = DateTime.Now;
            Console.WriteLine("EVENT!");
            //Only every full hour:
            var min = time.Minute;
            var offset = 5;

            if (min >  60 - offset || min < 0+offset)
            {
                foreach (var user in settings.users.registeredUsers)
                {
                    if (settings.pressure.TryGetValue(user, out var press))
                    {
                        if (time.Hour > press.startTime.Hour && time.Hour < press.endTime.Hour)
                        {
                            SendPressure(user);
                        }
                    }
                }
            }
        }


        static void BotOnMessage(object sender, MessageEventArgs e)
        {
            if (e.Message.Text != null)
            {
                Console.WriteLine($"Received a text message in chat {e.Message.Chat.Id}.");
                if(e.Message.Text[0] == '/')
                {
                    var text = e.Message.Text;
                    ParseCommand(e, text);
                }
            }
        }

        private static async void ParseCommand(MessageEventArgs e, string text)
        {
            text = text.Substring(1);
            Console.WriteLine("Command");

            //split into two substrings
            var par = text.Split(' ', 2);
            var user = e.Message.Chat.Id;

            PressureCollection press;
            switch (par[0])
            {
                case "register":
                    Console.WriteLine("Register");

                    if (!settings.users.registeredUsers.Contains(e.Message.Chat.Id))
                    {
                        settings.dirty = true;
                        settings.users.registeredUsers.Add(e.Message.Chat.Id);

                        settings.pressure.Add(user, new PressureCollection());
                    }
                    Console.WriteLine(e.Message.Chat.Id);
                    await bot.SendTextMessageAsync(
                     chatId: e.Message.Chat,
                     text: "Registered! "
                    );
                    SendPressure(e.Message.Chat.Id);
                    break;
                case "add":
                    if (settings.pressure.TryGetValue(user, out press))
                    {
                        settings.dirty = true;
                        press.pressures.Add(par[1]);
                    }
                    break;
                case "remove":
                   
                    if (int.TryParse(par[1], out var id))
                    {
                        if (settings.pressure.TryGetValue(user, out press))
                        {
                            if (id > 0 && press.pressures.Count >= id)
                            {
                                settings.dirty = true;
                                press.pressures.RemoveAt(id - 1);
                            }
                        }
                    }
                    break;
                case "show":
                    SendPressure(e.Message.Chat.Id);
                    break;
                default:
                    break;
            }
        }
    }
}
