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
using static System.Net.Mime.MediaTypeNames;
using System.Drawing.Imaging;
using XmpCore.Impl.XPath;
using System.Drawing;
using System.Diagnostics.Tracing;

// based on example code from
// https://www.codeguru.com/azure/analyzing-image-content-programmatically-using-the-microsoft-cognitive-vision-api/#Item1



namespace ImageContentDetector
{
    class Options
    {
        [Option('p', "path", Required = true, HelpText = "Path to a directory to be traversed. All JPGs in that directory (and its child directories) will be analyzed")]
        public string Path { get; set; }

        [Option('t', "timeout", Required = false, HelpText = "Time in milliseconds to wait between making API calls to Microsoft's Computer Vision API. This can also be defined in App.config. Default is 3000, or 3 seconds")]
        public string Timeout { get; set; }

        [Option('e', "endpoint", Required = false, HelpText = "Endpoint to be called to make request (e.g. https://switzerlandwest.api.cognitive.microsoft.com/vision/v1.0/analyze). This can also be defined in App.config")]
        public string Endpoint { get; set; }

        [Option('k', "key", Required = false, HelpText = "API Key used to make call to Microsoft's Computer Vision API. This can also be defined in App.config")]
        public string Key { get; set; }

        [Option('s', "skip", Required = false, HelpText = "Completely skip images that already have a description set.")]
        public bool Skip { get; set; }

        [Option('f', "force", Required = false, HelpText = "Force writing of the generated description. By default, the newly generated description will only be written to the image if it doesn't already have a description. Using the -f flag, you force the new description to always overwrite any existing description.")]
        public bool Force { get; set; }

    }

    internal class Program
    {
        private static int Timeout = 3100;
        private static string Endpoint = null;
        private static string Key = null;
        private static string Path = null;
        private static bool Force = false;
        private static bool Skip = false;

        const int MaxFileSize = 2000000; // I believe the API limit is around 3MB; todo: make this configurable?

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

            Console.WriteLine("Calling endpoint " + Program.Endpoint);

            Console.WriteLine("Collecting JPGs (this may take a while)...");
            List<string> jpgs = ProcessDirectory(Program.Path);
            Console.WriteLine(jpgs.Count()+" JPGs found.");

            var tempDir = System.IO.Path.GetTempPath();
            var tempFilePath = System.IO.Path.Combine(tempDir, System.IO.Path.GetRandomFileName());

            int counter = 0;
            foreach (string imgPath in jpgs)
            {
                counter++;
                WriteFileInfoToConsole(counter, imgPath);

                if (Program.Skip && HasDescription(imgPath))
                {
                    Console.WriteLine("Image already has a description");
                    continue;
                }
                Console.WriteLine("Waiting to make API call...");
                Thread.Sleep(Program.Timeout);
                try
                {
                    var parameters = ConfigurationManager.AppSettings["Parameters"];
                    var httpClient = new HttpClient();
                    var uri = new Uri(Program.Endpoint+parameters+"&subscription-key="+Program.Key);

                    // resize images larger than 3MB
                    Stream fileStream = File.OpenRead(imgPath);
                    if (fileStream.Length > MaxFileSize)
                    {
                        Console.WriteLine("Image is too big. Resizing image from " + fileStream.Length + "...");
                        fileStream = ResizeImage(tempFilePath, fileStream);
                        Console.WriteLine("New size: " + fileStream.Length);
                    }

                    var imageContent = new StreamContent(fileStream);
                    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/*");

                    var response = await httpClient.PostAsync(uri, imageContent);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        Console.WriteLine(responseBody);
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
                        Console.WriteLine($"Reason: {response.ReasonPhrase}");

                        var ms = new MemoryStream();
                        await response.Content.CopyToAsync(ms);
                        ms.Seek(0, SeekOrigin.Begin);

                        var sr = new StreamReader(ms);
                        string responseContent = sr.ReadToEnd();
                        Console.WriteLine($"Content: {responseContent}");

                        if (response.ReasonPhrase == "Quota Exceeded")
                        {
                            Console.WriteLine("Since your quota is exceeded, subsequent calls will likely fail, and I will stop attempting more calls.");
                            Environment.Exit(0);
                        }

                    }

                    if (File.Exists(tempFilePath)) File.Delete(tempFilePath);

                }
                catch (Exception ex)
                {
                    Console.WriteLine("An exception occurred: " + ex.Message);
                }
            }
            Console.WriteLine("Finished, evaluated " + counter + " images.");
        }
            
        private static Stream ResizeImage(String tempFilePath, Stream fileStream)
        {
            System.Drawing.Image image = System.Drawing.Image.FromStream(fileStream);
            fileStream.Close();

            float scaleFactor = 1200f/Math.Max(image.Width, image.Height);
            int w = (int)Math.Round(scaleFactor * image.Width);
            int h = (int)Math.Round(scaleFactor * image.Height);

            ImageCodecInfo jpgEncoder = GetEncoder(ImageFormat.Jpeg);
            Encoder myEncoder = Encoder.Quality;
            EncoderParameters myEncoderParameters = new EncoderParameters(1);
            myEncoderParameters.Param[0] = new EncoderParameter(myEncoder, 60L); // set quality to 50%

            System.Drawing.Bitmap resizedImage = new System.Drawing.Bitmap(image, w, h);

            // Turn the resized image into a file
            // doing it without a temp file and storing it directly into a MemoryStream didn't work, API responded with
            // Content: {"code":"InvalidImageFormat","requestId":"f331cfe3-f0b3-4e1f-9355-6aa51621e3e1","message":"Input data is not a valid image."}
            // MemoryStream stream = new MemoryStream();
            // resizedImage.Save(stream, jpgEncoder, myEncoderParameters);
            resizedImage.Save(tempFilePath, jpgEncoder, myEncoderParameters);

            return File.OpenRead(tempFilePath);
        }
        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            // Get all the image codecs
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();

            // Find the codec with the specified format
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }

            // Return null if no codec is found
            return null;
        }


        private static void WriteFileInfoToConsole(int counter, string imgPath)
        {
            Console.WriteLine("---- Image " + counter + " ----");
            Console.WriteLine(imgPath);
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
            try
            {

                JsonElement categories = jsonObject.GetProperty("categories");
                foreach (JsonElement category in categories.EnumerateArray())
                {
                    string categoryName = category.GetProperty("name").GetString();
                    AddKeyword(keywords, categoryName);
                    if (categoryName == "people")
                    {
                        JsonElement detail = category.GetProperty("detail");
                        /*
                        JsonElement celebrities = detail.GetProperty("celebrities");
                        if (celebrities.GetProperty("propertyName").ValueKind != JsonValueKind.Undefined)
                        {
                            foreach (JsonElement celebrity in celebrities.EnumerateArray())
                            {
                                string name = celebrity.GetProperty("name").GetString();
                                AddKeyword(keywords, name);
                            }
                        }
                        */
                        JsonElement landmarks = detail.GetProperty("landmarks");
                        if (landmarks.GetProperty("propertyName").ValueKind != JsonValueKind.Undefined)
                        {
                            foreach (JsonElement landmark in landmarks.EnumerateArray())
                            {
                                string name = landmark.GetProperty("name").GetString();
                                AddKeyword(keywords, name);
                            }

                        }
                    }
                }

                JsonElement faces = jsonObject.GetProperty("faces");
                foreach (JsonElement face in faces.EnumerateArray())
                {
                    if (face.TryGetProperty("age", out JsonElement propertyValue))
                    {
                        AddKeyword(keywords, propertyValue.ToString());
                    }
                    if (face.TryGetProperty("gender", out propertyValue))
                    {
                        AddKeyword(keywords, propertyValue.ToString());
                    }
                }

                JsonElement colors = jsonObject.GetProperty("color");
                string dominantColorForeground = colors.GetProperty("dominantColorForeground").GetString();
                string dominantColorBackground = colors.GetProperty("dominantColorBackground").GetString();
                string accentColor = colors.GetProperty("accentColor").GetString();
                bool isBWImg = colors.GetProperty("isBWImg").GetBoolean();
                string imgType = isBWImg ? "Monochrome" : "ColorImage";
                AddKeyword(keywords, dominantColorForeground);
                AddKeyword(keywords, dominantColorBackground);
                AddKeyword(keywords, accentColor);
                AddKeyword(keywords, imgType);

                JsonElement dominantColors = colors.GetProperty("dominantColors");
                foreach (JsonElement dominantColor in dominantColors.EnumerateArray())
                {
                    string c = dominantColor.GetString();
                    AddKeyword(keywords, c);
                }

                JsonElement imageType = jsonObject.GetProperty("imageType");
                int clipArtType = imageType.GetProperty("clipArtType").GetInt16();
                if (clipArtType > 0) AddKeyword(keywords, "Clipart");
                int lineDrawingType = imageType.GetProperty("lineDrawingType").GetInt16();
                if (lineDrawingType > 0) AddKeyword(keywords, "Linedrawing");

                JsonElement objects = jsonObject.GetProperty("objects");
                foreach (JsonElement obj in objects.EnumerateArray())
                {
                    string objName = obj.GetProperty("object").GetString();
                    AddKeyword(keywords, objName);

                    JsonElement parent = obj;
                    bool cont = true;
                    while(cont) {
                        if (parent.TryGetProperty("parent", out JsonElement propertyValue))
                        {
                            objName = propertyValue.GetProperty("object").GetString();
                            AddKeyword(keywords, objName);
                            parent = propertyValue;
                        }
                        else
                        {
                            cont = false;
                        }

                    }
                }

                JsonElement tags = jsonObject.GetProperty("description").GetProperty("tags");
                foreach (JsonElement tag in tags.EnumerateArray())
                {
                    string tagName = tag.GetString();
                    AddKeyword(keywords, tagName);
                }

                tags = jsonObject.GetProperty("tags");
                foreach (JsonElement tag in tags.EnumerateArray())
                {
                    string tagName = tag.GetProperty("name").GetString();
                    AddKeyword(keywords, tagName);
                }

                JsonElement brands = jsonObject.GetProperty("brands");
                foreach (JsonElement brand in brands.EnumerateArray())
                {
                    string tagName = brand.GetProperty("name").GetString();
                    AddKeyword(keywords, tagName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
            return keywords;
        }
        private static void AddKeyword(List<String> keywords, string keyword)
        {
            if (!keywords.Contains(keyword.ToLower())) keywords.Add(keyword.ToLower());
        }

        private static bool HasDescription(string imgPath)
        {
            using (MagickImage image = new MagickImage(imgPath))
            {
                var iptcProfile = image.GetIptcProfile();
                if (iptcProfile == null) return false;
                return (iptcProfile.GetAllValues(IptcTag.Caption).Count() != 0);
            }
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
            Console.WriteLine("Searching in " + dirPath);
            List<string> jpgs = new List<string>();

            string[] extensions = { "*.jpg", /*"*.jpeg",*/ "*.jpe", "*.jif", /*"*.jfif",*/ "*.jfi" };
            foreach (string extension in extensions)
            {
                //, "*.*", SearchOption.TopDirectoryOnly))  // if I iterate in GetFiles directly, I get Illegal characters in path exceptions
                // this seems to be the most robust approach...
                List<String> filePaths = new List<String>();
                try
                {
                    //Console.WriteLine("Searching for extension " + extension);
                    string[] foundFilePaths = Directory.GetFiles(dirPath, extension);
                    //Console.WriteLine(foundFilePaths.Length + " found");
                    filePaths.AddRange(foundFilePaths);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(dirPath);
                    Console.WriteLine(ex.StackTrace);
                }
                /*
                try
                {
                    //Console.WriteLine("Searching for extension " + extension.ToUpper());
                    string[] foundFilePaths = Directory.GetFiles(dirPath, extension.ToUpper());
                    //Console.WriteLine(foundFilePaths.Length + " found");
                    filePaths.AddRange(foundFilePaths);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(dirPath);
                    Console.WriteLine(ex.StackTrace);
                }
                */
                jpgs.AddRange(filePaths);
                /*
                foreach (string file in filePaths)
                {
                    string ext = System.IO.Path.GetExtension(file).ToLower();
                    if (extensions.Contains(ext))
                        jpgs.Add(file);
                }
                */
            }

            foreach (string subDir in Directory.GetDirectories(dirPath))
            {
                jpgs.AddRange(ProcessDirectory(subDir));
            }
            
            return jpgs;
        }

        private static void StoreOpts(Options opts)
        {
            if (opts.Timeout != null)
            {
                Program.Timeout = int.Parse(opts.Timeout);
            } else
            {
                Program.Timeout = int.Parse(ConfigurationManager.AppSettings["TimeoutBetweenCalls"]);
            }
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
            Program.Skip = opts.Skip;
        }

        private static void HandleParseError(IEnumerable<Error> errs)
        {
            Console.WriteLine("Argument Parse Error");
            foreach(Error error in errs) Console.WriteLine(error.ToString());
        }

    }
}