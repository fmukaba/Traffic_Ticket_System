using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using Amazon.S3;
using Amazon.S3.Util;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace FinalTrafficDetector
{
    public class Function
    {
        IAmazonS3 S3Client { get; set; }


        private const string database = @"<database>
            <vehicle>
                <plate>6TRJ244</plate>
                <make>Ford</make>
                <model>Focus</model>
                <color>Red</color>
                <owner>
                    <name>John Smith</name>
                    <phone>fxkikomina@gmail.com</phone>
                </owner>
            </vehicle>
            <vehicle>
                <plate>5ALN015</plate>
                <make>Honda</make>
                <model>Civic</model>
                <color>Blue</color>
                <owner>
                    <name>Jennifer Hartley</name>
                    <phone>fxkikomina@gmail.com</phone>
                </owner>
            </vehicle>
            <vehicle>
                <plate>7TRR812</plate>
                <make>Jeep</make>
                <model>Wrangler</model>
                <color>Yellow</color>
                <owner>
                    <name>Matt Johnson</name>
                    <phone>+14253652945</phone>
                </owner>
            </vehicle>
            <vehicle>
                <plate>3ZZB646</plate>
                <make>Honda</make>
                <model>CRV</model>
                <color>Silver</color>
                <owner>
                    <name>Dawn Fink</name>
                    <phone>+14253652945</phone>
                </owner>
            </vehicle>
            <vehicle>
                <plate>6YMX832</plate>
                <make>Chevrolet</make>
                <model>Cruze</model>
                <color>Red</color>
                <owner>
                    <name>Tim Carpenter</name>
                    <phone>+14253652945</phone>
                </owner>
            </vehicle>
        </database>";

        /// <summary>
        /// Default constructor. This constructor is used by Lambda to construct the instance. When invoked in a Lambda environment
        /// the AWS credentials will come from the IAM role associated with the function and the AWS region will be set to the
        /// region the Lambda function is executed in.
        /// </summary>
        public Function()
        {
            S3Client = new AmazonS3Client();
        }

        /// <summary>
        /// Constructs an instance with a preconfigured S3 client. This can be used for testing the outside of the Lambda environment.
        /// </summary>
        /// <param name="s3Client"></param>
        public Function(IAmazonS3 s3Client)
        {
            this.S3Client = s3Client;
        }

        /// <summary>
        /// This method is called for every Lambda invocation. This method takes in an S3 event object and can be used 
        /// to respond to S3 notifications.
        /// </summary>
        /// <param name="evnt"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task<string> FunctionHandler(S3Event evnt, ILambdaContext context)
        {
            var s3Event = evnt.Records?[0].S3;
            if (s3Event == null)
            {
                return null;
            }

            try
            {
                AmazonRekognitionClient client = new AmazonRekognitionClient(RegionEndpoint.USEast1);
                // get the file's name from event
                string imageTitle = s3Event.Object.Key;
                DetectTextRequest q = new DetectTextRequest();
                // get the file from S3
                Image img = new Image()
                {
                    S3Object = getObject(imageTitle)
                };
                q.Image = img;
                // detect text from the image
                var task = client.DetectTextAsync(q, new System.Threading.CancellationToken());
                task.Wait();
                DetectTextResponse r = task.Result;
                string plate = "";
                // filter recognized text
                foreach (TextDetection t in r.TextDetections)
                { 
                    if (isCapitaLettersNumbers(t.DetectedText))
                    {
                        plate = t.DetectedText;
                        //send message to plate's owner 
                        sendMessage(plate);
                    }
                }
            }
            catch (Exception e)
            {
                context.Logger.LogLine($"Error getting object {s3Event.Object.Key} from bucket {s3Event.Bucket.Name}. Make sure they exist and your bucket is in the same region as this function.");
                context.Logger.LogLine(e.Message);
                context.Logger.LogLine(e.StackTrace);
                throw;
            }

            return "Lamda has returned";
        }

        // Find the contact to send the notification to
        private static void sendMessage(string plate)
        {
            try
            {
                // find owner's contact from local database
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(database);
                XmlElement rootElement = xmlDoc.DocumentElement;
                string contact = null;
                string message = "Your blue Ford Escort (license plate " + plate + ")  was involved in a traffic violation. A ticket will be mailed to your address.";
                XmlNode vehicle = rootElement.SelectSingleNode("vehicle[plate='" + plate + "']");
                if (vehicle != null)
                {
                    string vehicleXml = vehicle.OuterXml;
                    xmlDoc.LoadXml(vehicleXml);
                    XmlNode owner = xmlDoc.SelectSingleNode("/vehicle/owner/phone");
                    if (owner != null)
                    {
                        contact = owner.InnerText;
                    }
                }

                if (contact != null)
                {
                    // send ticket notification
                    snsPublish(contact, message);
                } else
                {
                    Console.WriteLine("Contact not found");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        // Notify through sms (or email)
        private static void snsPublish(string contact, string message)
        {
            AmazonSimpleNotificationServiceClient client = new AmazonSimpleNotificationServiceClient(RegionEndpoint.USEast1);
            string arn = "arn:aws:sns:xxxxxxxx:TrafficTicket";

            // subscribe user to the ticketing policy
            SubscribeRequest request = new SubscribeRequest(arn, "sms", contact); // ("sms" can be replaced by "email)
            var task = client.SubscribeAsync(request, new System.Threading.CancellationToken());
            task.Wait();

            // Publish the message
            PublishRequest p = new PublishRequest
            (
                   message: message,
                   topicArn: arn
            );
            var task2 = client.PublishAsync(p, new System.Threading.CancellationToken());
            task2.Wait();
            PublishResponse r = task2.Result;

            Console.WriteLine("PublishRequest: " + r.ResponseMetadata.RequestId);
            Console.WriteLine("Message sent succesfully.");
        }

        // return a file from S3
        private static S3Object getObject(string filename)
        {
            S3Object o = new S3Object()
            {
                Bucket = "licenselatesfx",
                Name = filename
            };
            return o;
        }
        
        // checks if a string is made up of only uppercase letters and numbers
        private static bool isCapitaLettersNumbers(string s)
        {
            System.Text.RegularExpressions.Regex rg1 = new System.Text.RegularExpressions.Regex("[^A-Z]");
            System.Text.RegularExpressions.Regex rg2 = new System.Text.RegularExpressions.Regex("[^0-9]");
            System.Text.RegularExpressions.Regex rg3 = new System.Text.RegularExpressions.Regex("[/ ^$|\\s +/]");

            if (rg1.IsMatch(s) && rg2.IsMatch(s) && !rg3.IsMatch(s))
            {
                return true;
            }
            return false;
        }
    }
}
