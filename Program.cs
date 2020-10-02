using System;
using System.Collections.Generic;
using System.IO;
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
using Telegram.Bot.Types;
using static System.Net.Mime.MediaTypeNames;

namespace TelePressure
{
    public class Settings
    {
        public Users users { get; set; }
        public PressureCollection pressure {get; set; }

        public string id { get; set; }

        [JsonIgnore]
        public bool dirty { get; set; }
    }
    public class Users
    {
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
        public List<string> pressures  { get; set; }
        public SimpleTime startTime { get; set; }
        public SimpleTime endTime { get; set; }

        
    }
    class Program
    {
        private const string SettingsPath = "./settings.json";

        static HashSet<long> users = new HashSet<long>();
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
                cancellationTokenSource.Cancel();
                OnApplicationExit();
            };

            Console.CancelKeyPress += (sender, e) =>
            {
                cancellationTokenSource.Cancel();
                OnApplicationExit();
            };


            //Load settings
            var jsonUtf8Bytes = System.IO.File.ReadAllBytes(SettingsPath);
            var readOnlySpan = new ReadOnlySpan<byte>(jsonUtf8Bytes);
            settings = JsonSerializer.Deserialize<Settings>(readOnlySpan);
            settings.dirty = false;

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
        }

        private static void OnApplicationExit()
        {
            Console.WriteLine("Shutting down");
            SaveSettings();
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

        private static void SaveSettings()
        {
            if (settings.dirty)
            {
                Console.WriteLine("Saving changes");
                var jsonOptions = new JsonSerializerOptions()
                {
                    WriteIndented = true
                };
                var jsonString = JsonSerializer.SerializeToUtf8Bytes(settings, jsonOptions);
                System.IO.File.WriteAllBytes(SettingsPath, jsonString);
            }
        }

        static void SendPressure(long u)
        {
            var s = new StringBuilder();
            int i = 0;
            
            
            foreach (var p in settings.pressure.pressures)
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
        private static void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            var time = DateTime.Now;
            Console.WriteLine("EVENT!");
            //Only every full hour:
            var min = time.Minute;
            var offset = 5;

            if (min >  60 - offset || min < 0+offset)
            { 
                if(time.Hour > settings.pressure.startTime.Hour && time.Hour< settings.pressure.endTime.Hour)
                {
                    foreach (var u in users)
                    {
                        SendPressure(u);
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

            switch (par[0])
            {
                case "register":
                    Console.WriteLine("Register");

                    if (!users.Contains(e.Message.Chat.Id))
                    {
                        settings.dirty = true;
                        users.Add(e.Message.Chat.Id);
                    }
                    Console.WriteLine(e.Message.Chat.Id);
                    await bot.SendTextMessageAsync(
                     chatId: e.Message.Chat,
                     text: "Registered! "
                    );
                    SendPressure(e.Message.Chat.Id);
                    break;
                case "add":
                    settings.dirty = true;
                    settings.pressure.pressures.Add(par[1]);
                    break;
                case "remove":
                    settings.dirty = true;
                    if (int.TryParse(par[1], out var id))
                    {
                        if (id > 0 && settings.pressure.pressures.Count >= id)
                        {
                            settings.pressure.pressures.RemoveAt(id - 1);
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
