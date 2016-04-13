using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Microsoft.ProjectOxford.Emotion;
using Microsoft.ProjectOxford.Emotion.Contract;
using System.Diagnostics;
using System.Text;
using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json;
using Windows.Devices.Geolocation;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;

namespace AcandoEmotionEngine
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();

            InitilizeWebcam();
            InitializeEmotionEngine();
            InitializeFaceEngine();
            InitializeIotHub();
            InitializeGeolocation();

            azureLoggingToggleButton.IsChecked = true;
        }

        private async void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            StorageFile picture = await TakePicture();

            if (picture != null)
            {
                var faces = UploadAndDetectFaces(picture);
                var emotions = UploadAndDetectEmotions(picture);

                var myEmoFace = GetEmoFaces(await emotions, await faces);
                //LogResultEmoFace(focusFace);
                logOutput.Text = LogEmoFaces(myEmoFace);
                //LogResult(myEmoFace.AllEmotions, myEmoFace.AllFaces);
                if (azureLoggingToggleButton.IsChecked == true)
                {
                    string azureuri = await UploadPictureToAzure(picture, true);
                    await LogEmotionResultIot(myEmoFace, azureuri, localLoggingToggleButton.IsChecked);
                }
            }
        }

        private void ShutdownButton_Click(object sender, RoutedEventArgs e)
        {
            // Exit app
            Application.Current.Exit(); 
        }

        #region Geocode
        private async void InitializeGeolocation()
        {
            var geolocator = new Geolocator();
            geolocator.DesiredAccuracyInMeters = 100;
            Geoposition position = await geolocator.GetGeopositionAsync();
            // reverse geocoding
            myLocation = new BasicGeoposition
            {
                Longitude = position.Coordinate.Longitude,
                Latitude = position.Coordinate.Latitude
            };

        }
        #endregion

        #region Webcam code
        /// <summary>
        /// Initializes the USB Webcam
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void InitilizeWebcam(object sender = null, RoutedEventArgs e = null)
        {
            try
            {
                //initialize the WebCam via MediaCapture object
                MediaCap = new MediaCapture();
                await MediaCap.InitializeAsync();

                // Set callbacks for any possible failure in TakePicture() logic
                MediaCap.Failed += new MediaCaptureFailedEventHandler(MediaCapture_Failed);

                AppStatus.Text = "Camera initialized...Waiting for input!";
            }
            catch (Exception ex)
            {
                AppStatus.Text = "Unable to initialize camera for audio/video mode: " + ex.Message;
            }

            return;
        }

        /// <summary>
        /// Takes a picture from the webcam
        /// </summary>
        /// <returns>StorageFile of image</returns>
        public async Task<StorageFile> TakePicture()
        {
            try
            {
                //captureImage is our Xaml image control (to preview the picture onscreen)
                CaptureImage.Source = null;

                //gets a reference to the file we're about to write a picture into
                StorageFile photoFile = await KnownFolders.PicturesLibrary.CreateFileAsync(
                    "EmotionPic.jpg", CreationCollisionOption.GenerateUniqueName);

                //use the MediaCapture object to stream captured photo to a file
                ImageEncodingProperties imageProperties = ImageEncodingProperties.CreateJpeg();
                await MediaCap.CapturePhotoToStorageFileAsync(imageProperties, photoFile);

                //show photo onscreen
                IRandomAccessStream photoStream = await photoFile.OpenReadAsync();
                BitmapImage bitmap = new BitmapImage();
                bitmap.SetSource(photoStream);
                CaptureImage.Width = bitmap.PixelWidth;
                CaptureImage.Height = bitmap.PixelHeight;
                CaptureImage.Source = bitmap;

                AppStatus.Text = "Took Photo: " + photoFile.Name;

                return photoFile;
            }

            catch (Exception ex)
            {
                //write the exception on screen
                AppStatus.Text = "Error taking picture: " + ex.Message;

                return null;
            }
        }

        /// <summary>
        /// Callback function for any failures in MediaCapture operations
        /// </summary>
        /// <param name="currentCaptureObject"></param>
        /// <param name="currentFailure"></param>
        private void MediaCapture_Failed(MediaCapture currentCaptureObject, MediaCaptureFailedEventArgs currentFailure)
        {
            AppStatus.Text = currentFailure.Message;
        }
        #endregion

        #region Azure Code
        /// <summary>
        /// Upload the StorageFile to Azure Blob Storage
        /// </summary>
        /// <param name="file">The StorageFile to upload</param>
        /// <returns>null</returns>
        private async Task<string> UploadPictureToAzure(StorageFile file, bool delfile = false)
        {
            try
            {
                string blobName = string.Format("photos/" + deviceId + "_{0:yyyy-MM-dd_HH-mm-ss_ff}.jpg", DateTime.Now);
                
                // Retrieve storage account from connection string.
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(blobConnection);
                // Create the blob client.
                CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();

                // Retrieve a reference to a container.
                CloudBlobContainer container = blobClient.GetContainerReference("iot");

                // Create the container if it doesn't already exist.
                await container.CreateIfNotExistsAsync();

                CloudBlockBlob blockBlob = container.GetBlockBlobReference(blobName);

                await blockBlob.UploadFromFileAsync(file);

                AppStatus.Text = blobName + " uploaded to Azure.";

                if (delfile)
                {
                    await file.DeleteAsync();
                }
                return blockBlob.StorageUri.PrimaryUri.AbsoluteUri;
            }
            catch (Exception ex)
            {
                AppStatus.Text = ex.ToString();
                return null;
            }
        }

        #endregion

        #region Face Code
        /// <summary>
        /// Upload the StorageFile to Azure Blob Storage
        /// </summary>
        /// <param name="file">The StorageFile to upload</param>
        /// <returns>null</returns>
        /// 

        private void InitializeEmotionEngine()
        {
            AppStatus.Text = "EmotionServiceClient is created";

            // Create Project Oxford Emotion API Service client.
            emotionServiceClient = new EmotionServiceClient(oxfordEmotionKey);
        }

        private async Task<Emotion[]> UploadAndDetectEmotions(StorageFile file)
        {
            AppStatus.Text = "Calling EmotionServiceClient.RecognizeAsync()...";
            try
            {
                Emotion[] emotionResult;
                var randomAccessStream = await file.OpenReadAsync();
                using (Stream imageFileStream = randomAccessStream.AsStreamForRead())
                {
                    //
                    // Detect the emotions in the URL.
                    //
                    emotionResult = await emotionServiceClient.RecognizeAsync(imageFileStream);
                    AppStatus.Text = "EmotionServiceClient.RecognizeAsync() succeeded!";
                    return emotionResult;
                }
            }
            catch (Exception ex)
            {
                AppStatus.Text = ex.ToString();
                return null;
            }
        }

        public void LogEmotionResult(Emotion[] emotionResult)
        {
            StringBuilder outputString = new StringBuilder();
            int emotionResultCount = 0;
            if (emotionResult != null && emotionResult.Length > 0)
            {
                foreach (Emotion emotion in emotionResult)
                {
                    outputString.AppendLine("Emotion[" + emotionResultCount + "]");
                    outputString.AppendLine("  FaceRectangle = left: " + emotion.FaceRectangle.Left
                             + ", top: " + emotion.FaceRectangle.Top
                             + ", width: " + emotion.FaceRectangle.Width
                             + ", height: " + emotion.FaceRectangle.Height);

                    outputString.AppendLine(String.Format("  Anger: {0:P2}.", emotion.Scores.Anger));
                    outputString.AppendLine(String.Format("  Contempt: {0:P2}.", emotion.Scores.Contempt));
                    outputString.AppendLine(String.Format("  Disgust: {0:P2}.", emotion.Scores.Disgust));
                    outputString.AppendLine(String.Format("  Fear: {0:P2}.", emotion.Scores.Fear));
                    outputString.AppendLine(String.Format("  Happiness: {0:P2}.", emotion.Scores.Happiness));
                    outputString.AppendLine(String.Format("  Neutral: {0:P2}.", emotion.Scores.Neutral));
                    outputString.AppendLine(String.Format("  Sadness: {0:P2}.", emotion.Scores.Sadness));
                    outputString.AppendLine(String.Format("  Surprise: {0:P2}.", emotion.Scores.Surprise));
                    outputString.AppendLine(String.Format("Long: {0:0.###}, Lat: {1:0.###}.", myLocation.Longitude, myLocation.Latitude));
                    emotionResultCount++;
                }
            }
            else
            {
                outputString.AppendLine("No emotion is detected. This might be due to:\n" +
                    "    image is too small to detect faces\n" +
                    "    no faces are in the images\n" +
                    "    faces poses make it difficult to detect emotions\n" +
                    "    or other factors");
            }

            logOutput.Text = outputString.ToString();
        }

        public class EmoFace
        {
            public FaceAttributes FaceAttributes { get; set; }
            public Guid FaceId { get; set; }
            public FaceLandmarks FaceLandmarks { get; set; }
            public FaceRectangle FaceRectangle { get; set; }
            public Scores Scores { get; set; }
        }

        private void InitializeFaceEngine()
        {
            AppStatus.Text = "FaceServiceClient is created";

            // Create Project Oxford Face API Service client.
            faceServiceClient = new FaceServiceClient(oxfordFaceKey);
        }

        private async Task<Face[]> UploadAndDetectFaces(StorageFile file)
        {
            AppStatus.Text = "Calling FaceServiceClient.RecognizeAsync()...";
            try
            {
                Face[] faceResult;
                var randomAccessStream = await file.OpenReadAsync();
                using (Stream imageFileStream = randomAccessStream.AsStreamForRead())
                {
                    //
                    // Detect the faces in the URL.
                    //
                    faceResult = await faceServiceClient.DetectAsync(imageFileStream, false, true, new FaceAttributeType[] { FaceAttributeType.Gender, FaceAttributeType.Age, FaceAttributeType.FacialHair, FaceAttributeType.Smile });
                    return faceResult;
                }
            }
            catch (Exception ex)
            {
                AppStatus.Text = ex.ToString();
                Debug.WriteLine(ex.ToString());
                return null;
            }
        }

        public List<EmoFace> GetEmoFaces(Emotion[] emotions, Face[] faces)
        {
            int numberOfFaces = faces.Length;
            List<EmoFace> emoFaces = new List<EmoFace>();

            if (numberOfFaces > 0)
            {
                for (int i = 0; i < numberOfFaces; i++)
                {
                    EmoFace emoFace = new EmoFace();
                    Debug.WriteLine(faces[i].FaceAttributes.Gender);
                    emoFace.FaceAttributes = faces[i].FaceAttributes;
                    emoFace.FaceId = faces[i].FaceId;
                    emoFace.FaceLandmarks = faces[i].FaceLandmarks;
                    emoFace.FaceRectangle = faces[i].FaceRectangle;
                    emoFace.Scores = emotions[i].Scores;
                    emoFaces.Add(emoFace);
                }
            }

            return emoFaces;
        }

        public void LogFaceResult(Face[] faceResult)
        {
            StringBuilder outputString = new StringBuilder();
            int faceResultCount = 0;
            if (faceResult != null && faceResult.Length > 0)
            {
                foreach (Face face in faceResult)
                {
                    outputString.AppendLine("Face[" + faceResultCount + "]");
                    outputString.AppendLine("  FaceRectangle = left: " + face.FaceRectangle.Left
                             + ", top: " + face.FaceRectangle.Top
                             + ", width: " + face.FaceRectangle.Width
                             + ", height: " + face.FaceRectangle.Height);

                    outputString.AppendLine(String.Format("  Age: {0:0.###}.", face.FaceAttributes.Age));
                    outputString.AppendLine("  Gender: " + face.FaceAttributes.Gender);
                    //outputString.AppendLine(String.Format("Long: {0:0.###}, Lat: {1:0.###}.", myLocation.Longitude, myLocation.Latitude));
                    faceResultCount++;
                }
            }
            else
            {
                outputString.AppendLine("No faces is detected. This might be due to:\n" +
                    "    image is too small to detect faces\n" +
                    "    no faces are in the images\n" +
                    "    faces poses make it difficult to detect faces\n" +
                    "    or other factors");
            }

            logOutput.Text = outputString.ToString();
        }

        #endregion

        #region IoTHub
        /// <summary>
        /// Log events to to Azure IoT Hub
        /// </summary>
        /// <param name="message">The Json message to upload</param>
        /// <returns>null</returns>
        /// 
        private void InitializeIotHub()
        {
            AppStatus.Text = "IoT Hub connection is up."; 

            // Create IoT hub device client.
            deviceClient = DeviceClient.Create(iotHubUri, new DeviceAuthenticationWithRegistrySymmetricKey("myFirstDevice", deviceKey), TransportType.Http1);
        }

        private static async void SendDeviceToCloudMessagesAsync(string messageString)
        {

            var message = new Message(Encoding.ASCII.GetBytes(messageString));

            await deviceClient.SendEventAsync(message);

        }

        public async Task LogEmotionResultIot(List<EmoFace> emoFaces, string uri, bool? localLogging)
        {
            var geolocator = new Geolocator();
            geolocator.DesiredAccuracyInMeters = 100;
            Geoposition position = await geolocator.GetGeopositionAsync();
            // reverse geocoding
            BasicGeoposition myLocation = new BasicGeoposition
            {
                Longitude = position.Coordinate.Longitude,
                Latitude = position.Coordinate.Latitude
            };
            var collection = new
            {
                EventId = Guid.NewGuid(),
                DeviceId = deviceId,
                Location = myLocation,
                Timestamp = System.DateTime.Now,
                AzureUri = uri,
                Faces = emoFaces
            };

            string message = JsonConvert.SerializeObject(collection);

            if (localLogging == true)
            {
                string logFileName = collection.EventId.ToString() + ".json";
                StorageFile logFile = await KnownFolders.PicturesLibrary.CreateFileAsync(logFileName);
                await FileIO.WriteTextAsync(logFile, message);
            }

            try
            {
                SendDeviceToCloudMessagesAsync(message);
            }
            catch (Exception ex)
            {
                AppStatus.Text = ex.ToString();
            }
        }
        #endregion

        #region Logging
        public string LogFocusFace(EmoFace focusFace)
        {
            StringBuilder outputString = new StringBuilder();
            if (focusFace == null)
            {
                outputString.AppendLine("No faces is detected. This might be due to:\n" +
                    "    image is too small to detect faces\n" +
                    "    no faces are in the images\n" +
                    "    faces poses make it difficult to detect faces\n" +
                    "    or other factors");
            }
            else
            {
                outputString.AppendLine("Face");
                outputString.AppendLine("  FaceRectangle = left: " + focusFace.FaceRectangle.Left
                         + ", top: " + focusFace.FaceRectangle.Top
                         + ", width: " + focusFace.FaceRectangle.Width
                         + ", height: " + focusFace.FaceRectangle.Height);

                outputString.AppendLine(String.Format("  Age: {0:0.###}.", focusFace.FaceAttributes.Age));
                outputString.AppendLine("  Gender: " + focusFace.FaceAttributes.Gender);
                outputString.AppendLine(String.Format("  Anger: {0:P2}.", focusFace.Scores.Anger));
                outputString.AppendLine(String.Format("  Contempt: {0:P2}.", focusFace.Scores.Contempt));
                outputString.AppendLine(String.Format("  Disgust: {0:P2}.", focusFace.Scores.Disgust));
                outputString.AppendLine(String.Format("  Fear: {0:P2}.", focusFace.Scores.Fear));
                outputString.AppendLine(String.Format("  Happiness: {0:P2}.", focusFace.Scores.Happiness));
                outputString.AppendLine(String.Format("  Neutral: {0:P2}.", focusFace.Scores.Neutral));
                outputString.AppendLine(String.Format("  Sadness: {0:P2}.", focusFace.Scores.Sadness));
                outputString.AppendLine(String.Format("  Surprise: {0:P2}.", focusFace.Scores.Surprise));
            }

            return outputString.ToString();
        }

        public string LogEmoFaces(List<EmoFace> emoFaces)
        {
            StringBuilder outputString = new StringBuilder();
            if (emoFaces.Count == 0)
            {
                outputString.AppendLine("No emotion is detected. This might be due to:\n" +
                        "    image is too small to detect faces\n" +
                        "    no faces are in the images\n" +
                        "    faces poses make it difficult to detect emotions\n" +
                        "    or other factors");
            }
            else
            {
                int analyzeResultCount = emoFaces.Count;
                for (int i = 0; i < analyzeResultCount; i++)
                {
                    outputString.AppendLine("Face: " + i);
                    outputString.AppendLine("  FaceRectangle = left: " + emoFaces[i].FaceRectangle.Left
                             + ", top: " + emoFaces[i].FaceRectangle.Top
                             + ", width: " + emoFaces[i].FaceRectangle.Width
                             + ", height: " + emoFaces[i].FaceRectangle.Height);

                    outputString.AppendLine(String.Format("  Age: {0:0.###}.", emoFaces[i].FaceAttributes.Age));
                    outputString.AppendLine("  Gender: " + emoFaces[i].FaceAttributes.Gender);
                    outputString.AppendLine(String.Format("  Anger: {0:P2}.", emoFaces[i].Scores.Anger));
                    outputString.AppendLine(String.Format("  Contempt: {0:P2}.", emoFaces[i].Scores.Contempt));
                    outputString.AppendLine(String.Format("  Disgust: {0:P2}.", emoFaces[i].Scores.Disgust));
                    outputString.AppendLine(String.Format("  Fear: {0:P2}.", emoFaces[i].Scores.Fear));
                    outputString.AppendLine(String.Format("  Happiness: {0:P2}.", emoFaces[i].Scores.Happiness));
                    outputString.AppendLine(String.Format("  Neutral: {0:P2}.", emoFaces[i].Scores.Neutral));
                    outputString.AppendLine(String.Format("  Sadness: {0:P2}.", emoFaces[i].Scores.Sadness));
                    outputString.AppendLine(String.Format("  Surprise: {0:P2}.", emoFaces[i].Scores.Surprise));
                }
            }
            return outputString.ToString();
        }

        public void LogResult(Emotion[] emotionResult, Face[] faceResult)
        {
            StringBuilder outputString = new StringBuilder();
            int analyzeResultCount = faceResult.Length;
            for (int i = 0; i < analyzeResultCount; i++)
            {
                Emotion emotion = new Emotion();
                Face face = new Face();
                if (emotionResult.Length > i)
                    emotion = emotionResult[i];
                if (faceResult.Length > i)
                    face = faceResult[i];
                if (emotion != null)
                {
                    outputString.AppendLine("Face[" + i + "]");
                    outputString.AppendLine("  FaceRectangle = left: " + emotion.FaceRectangle.Left
                                + ", top: " + emotion.FaceRectangle.Top
                                + ", width: " + emotion.FaceRectangle.Width
                                + ", height: " + emotion.FaceRectangle.Height);

                    outputString.AppendLine(String.Format("  Anger: {0:P2}.", emotion.Scores.Anger));
                    outputString.AppendLine(String.Format("  Contempt: {0:P2}.", emotion.Scores.Contempt));
                    outputString.AppendLine(String.Format("  Disgust: {0:P2}.", emotion.Scores.Disgust));
                    outputString.AppendLine(String.Format("  Fear: {0:P2}.", emotion.Scores.Fear));
                    outputString.AppendLine(String.Format("  Happiness: {0:P2}.", emotion.Scores.Happiness));
                    outputString.AppendLine(String.Format("  Neutral: {0:P2}.", emotion.Scores.Neutral));
                    outputString.AppendLine(String.Format("  Sadness: {0:P2}.", emotion.Scores.Sadness));
                    outputString.AppendLine(String.Format("  Surprise: {0:P2}.", emotion.Scores.Surprise));
                }
                else
                {
                    outputString.AppendLine("No emotion is detected. This might be due to:\n" +
                        "    image is too small to detect faces\n" +
                        "    no faces are in the images\n" +
                        "    faces poses make it difficult to detect emotions\n" +
                        "    or other factors");
                }
                if (face != null)
                {
                    outputString.AppendLine(String.Format("  Age: {0:0.###}.", face.FaceAttributes.Age));
                    outputString.AppendLine("  Gender: " + face.FaceAttributes.Gender);
                }
                else
                {
                    outputString.AppendLine("No faces is detected. This might be due to:\n" +
                        "    image is too small to detect faces\n" +
                        "    no faces are in the images\n" +
                        "    faces poses make it difficult to detect faces\n" +
                        "    or other factors");
                }
            }

            outputString.AppendLine(String.Format("Long: {0:0.###}, Lat: {1:0.###}.", myLocation.Longitude, myLocation.Latitude));
            logOutput.Text = outputString.ToString();
        }
        #endregion
    }
}