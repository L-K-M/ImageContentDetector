using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Configuration;
using System.Text.Json;
using ImageMagick;
using System.Threading;
using CommandLine;
using System.Linq;

// based on example code from
// https://www.codeguru.com/azure/analyzing-image-content-programmatically-using-the-microsoft-cognitive-vision-api/#Item1



namespace ImageContentDetector
{
    class Options
    {
        [Option('p', "path", Required = true, HelpText = "Path to a directory to be traversed. All JPGs in that directory (and its child directories) will be analyzed")]
        public string Path { get; set; }

        [Option('t', "timeout", Required = false, HelpText = "Time in milliseconds to wait between making API calls to Microsoft's Computer Vision API. Default is 18000")]
        public string Timeout { get; set; }

        [Option('e', "endpoint", Required = false, HelpText = "Endpoint to be called to make request (e.g. https://switzerlandwest.api.cognitive.microsoft.com/vision/v1.0/analyze). This can also be defined in App.config")]
        public string Endpoint { get; set; }

        [Option('k', "key", Required = false, HelpText = "API Key used to make call to Microsoft's Computer Vision API. This can also be defined in App.config")]
        public string Key { get; set; }

        [Option('f', "force", Required = false, HelpText = "Force writing of the generated description. By default, the newly generated description will only be written to the image if it doesn't already have a description. Using the -f flag, you force the new description to always overwrite any existing description.")]
        public bool Force { get; set; }

    }

    internal class Program
    {
        private static int Timeout = 18000;
        private static string Endpoint = null;
        private static string Key = null;
        private static string Path = null;
        private static bool Force = false;

        static async Task Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed<Options>(opts => StoreOpts(opts))
                .WithNotParsed<Options>((errs) => HandleParseError(errs));

            if (!Directory.Exists(Program.Path))
            {
                Console.WriteLine("The specified directory does not exist.");
                return;
            }

            Console.WriteLine("Collecting JPGs (this may take a while)...");
            List<string> jpgs = ProcessDirectory(Program.Path);
            Console.WriteLine(jpgs.Count()+" JPGs found.");

            int counter = 0;
            foreach (string imgPath in jpgs)
            {
                counter++;
                WriteFileInfoToConsole(counter, imgPath);
                Thread.Sleep(Program.Timeout);
                try
                {
                    var parameters = ConfigurationManager.AppSettings["Parameters"];
                    var httpClient = new HttpClient();
                    var uri = new Uri(Program.Endpoint+parameters+"&subscription-key="+Program.Key);

                    var imageContent = new StreamContent(File.OpenRead(imgPath));
                    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/*");

                    var response = await httpClient.PostAsync(uri, imageContent);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        JsonElement jsonObject = JsonSerializer.Deserialize<JsonElement>(responseBody);

                        string description = GetDescription(jsonObject);
                        List<String> keywords = GetKeywords(jsonObject);

                        Console.WriteLine("Description: "+description);
                        Console.WriteLine("Tags: " + string.Join(", ", keywords.ToArray()));

                        // exif tags: description, caption-_abstract, keywords, subject

                        StoreMetadata(imgPath, description, keywords);
                    }
                    else
                    {
                        Console.WriteLine($"Error: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("An exception occurred: " + ex.Message);
                }
            }
            Console.WriteLine("Finished, evaluated " + counter + " images.");
        }

        private static void WriteFileInfoToConsole(int counter, string imgPath)
        {
            Console.WriteLine("---- Image " + counter + " ----");
            Console.WriteLine(imgPath);
            Console.WriteLine("Waiting to make API call...");
        }

        private static string GetDescription(JsonElement jsonObject)
        {
            JsonElement captions = jsonObject.GetProperty("description").GetProperty("captions");
            foreach (JsonElement caption in captions.EnumerateArray())
            {
                string captionText = caption.GetProperty("text").GetString();
                return captionText;
            }
            return "";
        }
        private static List<String> GetKeywords(JsonElement jsonObject)
        {
            List<String> keywords = new List<String>();

            JsonElement categories = jsonObject.GetProperty("categories");
            foreach (JsonElement category in categories.EnumerateArray())
            {
                string categoryName = category.GetProperty("name").GetString();
                if (!keywords.Contains(categoryName)) keywords.Add(categoryName);
            }
            JsonElement tags = jsonObject.GetProperty("description").GetProperty("tags");
            foreach (JsonElement tag in tags.EnumerateArray())
            {
                string tagName = tag.GetString();
                if (!keywords.Contains(tagName)) keywords.Add(tagName);
            }
            return keywords;
        }
        private static void StoreMetadata(string imgPath, string description, List<String> keywords)
        {
            using (MagickImage image = new MagickImage(imgPath))
            {
                bool changesWereMade = false;

                var iptcProfile = image.GetIptcProfile();
                if (iptcProfile == null)
                {
                    iptcProfile = new IptcProfile();
                }
                // Update the "ImageDescription" tag with a new value
                if(Program.Force || iptcProfile.GetAllValues(IptcTag.Caption).Count() == 0)
                {
                    iptcProfile.SetValue(IptcTag.Caption, description);
                    changesWereMade = true;
                }

                var existingKeywordsIptc = iptcProfile.GetAllValues(IptcTag.Keyword);
                var existingKeywords = new List<string>();
                foreach (IIptcValue value in existingKeywordsIptc)
                {
                    existingKeywords.Add(value.Value);
                }
                foreach (string keyword in keywords)
                {
                    if (!existingKeywords.Contains(keyword))
                    {
                        iptcProfile.SetValue(IptcTag.Keyword, keyword);
                        changesWereMade = true;
                    }
                }

                if (changesWereMade)
                {
                    Console.WriteLine("Changes were made to metadata, storing image file...");

                    // Save the modified EXIF metadata back to the image
                    image.SetProfile(iptcProfile);

                    // Save the modified image
                    image.Write(imgPath);
                }
            }

        }

        private static List<string> ProcessDirectory(string dirPath)
        {
            List<string> jpgs = new List<string>();

            string[] extensions = { ".jpg", ".jpeg", ".jpe", ".jif", ".jfif", ".jfi" };
            foreach (string file in Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories))
            {
                string ext = System.IO.Path.GetExtension(file).ToLower();
                if (extensions.Contains(ext))
                    jpgs.Add(file);
            }

            //foreach (string subDir in Directory.GetDirectories(dirPath))
            //{
            //    jpgs.AddRange(ProcessDirectory(subDir));
            //}
            
            return jpgs;
        }

        private static void StoreOpts(Options opts)
        {
            if(opts.Timeout != null) Program.Timeout = int.Parse(opts.Timeout);
            if (opts.Key != null)
            {
                Program.Key = opts.Key;
            } else
            {
                Program.Key = ConfigurationManager.AppSettings["Key"];

            }
            if (opts.Endpoint != null)
            {
                Program.Endpoint = opts.Endpoint;
            }
            else
            {
                Program.Endpoint = ConfigurationManager.AppSettings["Endpoint"];

            }
            Program.Path = opts.Path;
            Program.Force = opts.Force;
        }

        private static void HandleParseError(IEnumerable<Error> errs)
        {
            Console.WriteLine("Argument Parse Error");
            foreach(Error error in errs) Console.WriteLine(error.ToString());
        }

    }
}