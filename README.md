# ImageContentDetector

This simple command line tool iterates through a directory, collects all JPEG files, uses Microsoft's Computer Vision API to detect what the image contains, and set exif metadata for the description and keywords.

The purpose of this is to make it easier to find images on systems that don't provide image content search.

**Note: when using this tool, your images will be sent to Microsoft's Computer Vision API on Azure. If you are not comfortable with this, do not use this tool.**

## Computer Vision API

You must provide an API key to use the Computer Vision API, which you can get here:  
[https://azure.microsoft.com/en-us/free/cognitive-services/?api=computer-vision%20%20](https://azure.microsoft.com/en-us/free/cognitive-services/?api=computer-vision%20%20)

A free key allows you to make 5000 requests per day.

After retrieving the key, edit App.config to set the key and the endpoint (which will be something like https://[location].api.cognitive.microsoft.com/vision/v1.0/analyze), or pass these values to the command line when invoking it.

## Usage

Example:  
.\ImageContentDetector.exe -p "C:\Users\yourname\Pictures"

  -p, --path        Required. Path to a directory to be traversed. All JPGs in that directory (and its child directories) will be analyzed

  -t, --timeout     Time in milliseconds to wait between making API calls to Microsoft's Computer Vision API. Default is 18000

  -e, --endpoint    Endpoint to be called to make request (e.g. https://switzerlandwest.api.cognitive.microsoft.com/vision/v1.0/analyze). This can also be defined in App.config

  -k, --key         API Key used to make call to Microsoft's Computer Vision API. This can also be defined in App.config

  -s, --skip        Completely skip images that already have a description set

  -f, --force       Force writing of the generated description. By default, the newly generated description will only be written to the image if it doesn't already have a description. Using the -f flag, you force the new description to always overwrite any existing description.

  --help            Display this help screen.

  --version         Display version information.