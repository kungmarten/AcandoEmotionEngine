using Microsoft.ProjectOxford.Emotion;
using Windows.Media.Capture;
using Microsoft.Azure.Devices.Client;
using Windows.Devices.Geolocation;
using Microsoft.ProjectOxford.Face;
using Windows.Storage;

namespace AcandoEmotionEngine
{
    public sealed partial class MainPage
    {
        // Azure constants.
        private readonly string iotHubUri = "";
        private readonly string deviceKey = ""; 
        private readonly string deviceId = "";
        private static DeviceClient deviceClient;
        private readonly string blobConnection = "";

        // Oxford constants.
        private readonly string oxfordEmotionKey = "";
        EmotionServiceClient emotionServiceClient;
        private readonly string oxfordFaceKey = "";
        IFaceServiceClient faceServiceClient;

        // Webcam variables.
        private MediaCapture MediaCap;
        private bool IsInPictureCaptureMode = false;

        // Geolocation constants.
        BasicGeoposition myLocation;
    }
}