using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using DySense;
using EDSDKLib;

namespace DySenseCanonEDSDK
{
    public class CanonEDSDKDriver : SensorBase
    {
        // How often to trigger a new image (in seconds).
        double imageTriggerPeriod = 0;

        // Time stamps when camera was last triggered.
        double lastTriggerUtcTime = 0;
        double lastTriggerSysTime = 0;

        List<Camera> CameraList;
        SDKHandler CameraHandler;

        bool receivedNewImage = true;

        bool cameraErrorOccurred = false;

        // Where to save output images to.
        string outDirectory;

        double TimeSinceLastTrigger { get { return SysTime - lastTriggerSysTime; } }

        public CanonEDSDKDriver(string sensorID, string instrumentID, Dictionary<string, object> settings, string connectEndpoint)
            : base(sensorID, instrumentID, connectEndpoint, decideTimeout: false)
        {
            this.outDirectory = Convert.ToString(settings["out_directory"]);
            this.imageTriggerPeriod = Convert.ToDouble(settings["trigger_period"]);

            // The desired read period is set much faster than actual trigger period so we can constantly check for SDK events.
            base.DesiredReadPeriod = 0.1; // seconds
            base.MaxClosingTime = 4.0; // seconds
        }

        protected override void Setup()
        {
            if (!Directory.Exists(outDirectory))
            {
                Directory.CreateDirectory(outDirectory);
            }

            CameraHandler = new SDKHandler();
            //CameraHandler.SDKObjectEvent += handler_SDKObjectEvent;
            CameraHandler.CameraAdded += new SDKHandler.CameraAddedHandler(SDK_CameraAdded);
            CameraHandler.CameraHasShutdown += SDK_CameraHasShutdown;
            CameraHandler.NewImageDownloaded += CameraHandler_NewImageDownloaded;
            CameraHandler.CameraError += CameraHandler_CameraError;

            // Tell the handler where to save the images.
            CameraHandler.ImageSaveDirectory = outDirectory;

            RefreshCamera();
        }

        protected void CheckForEdsdkEvents()
        {
            EDSDK.EdsGetEvent();
            if (this.cameraErrorOccurred)
            {
                throw new Exception("Unhandled camera error.");
            }
        }

        void CameraHandler_CameraError(uint errorID)
        {
            // I think this is running from the EDSDK thread.
            if (errorID == 0x81)
            {
                return; // this is from overtriggering so don't worry about it.
            }
            SendText(String.Format("Camera Error 0x{0}", errorID.ToString("X")));
            this.cameraErrorOccurred = true;
        }

        void CameraHandler_NewImageDownloaded(string filePath)
        {
            // TODO rename and just log new filename.
            SendText(String.Format("Downloaded {0}", filePath));
            HandleData(UtcTime, SysTime, new List<object>() { filePath });

            // Set flag so we know we can trigger again.
            receivedNewImage = true;
        }

        private void SDK_CameraAdded()
        {
            SendText("New camera added.");
            RefreshCamera();
        }

        private void SDK_CameraHasShutdown(object sender, EventArgs e)
        {
            SendText("Camera has shutdown.");
            CloseSession();
        }

        private uint handler_SDKObjectEvent(uint inEvent, IntPtr inRef, IntPtr inContext)
        {
            //handle object event here
            switch (inEvent)
            {
                case EDSDK.ObjectEvent_DirItemRequestTransfer:
                    //CameraHandler.DownloadImage(inRef, ImageSaveDirectory);
                    break;
            }
            return EDSDK.EDS_ERR_OK;
        }

        private void OpenSession()
        {
            if (CameraList.Count == 0) 
            {
                SendText("Can't open cam session because no valid cameras.");
                return;
            }
            CameraHandler.OpenSession(CameraList[0]);
            string cameraName = CameraHandler.MainCamera.Info.szDeviceDescription;
            SendText(String.Format("Opened session with {0}", cameraName));
            if (CameraHandler.GetSetting(EDSDK.PropID_AEMode) != EDSDK.AEMode_Manual)
            {
                SendText("Camera is not in manual mode. Some features might not work!");
            }
            // Tell the camera to send back images instead of saving to card.
            CameraHandler.SetSetting(EDSDK.PropID_SaveTo, (uint)EDSDK.EdsSaveTo.Host);
            CameraHandler.SetCapacity();

            CheckForEdsdkEvents();
        }

        private void CloseSession()
        {
            CameraHandler.CloseSession();
            RefreshCamera();
        }

        private void RefreshCamera()
        {
            CameraList = CameraHandler.GetCameraList();
            if (!CameraHandler.CameraSessionOpen)
            {
                OpenSession();
            }
        }

        protected override void Close()
        {
            if (CameraHandler != null)
            {
                EDSDK.EdsGetEvent();
                CameraHandler.CloseSession();
                CameraHandler.Dispose();
                CameraHandler = null;
            }
        }

        protected override bool IsClosed()
        {
            return CameraHandler == null;
        }

        protected override void RequestNewData()
        {
            CheckForEdsdkEvents();

            if (!CameraHandler.CameraSessionOpen) { return; }

            // Make sure we should be triggering at all.
            if (!ShouldRecordData()) { return; }

            // Don't trigger again until we've gotten the last image.
            if (!receivedNewImage) { return; }

            // Wait until enough time has elapsed before triggering again.
            if (TimeSinceLastTrigger < imageTriggerPeriod) { return; }

            Trigger();

            // Set flag so we won't trigger again until we've downloaded our first image.
            receivedNewImage = false;
        }

        protected override string ReadNewData()
        {
            CheckForEdsdkEvents();

            if (!CameraHandler.CameraSessionOpen)
            {
                return "timed_out";
            }

            if (!ShouldRecordData())
            {
                // Shouldn't be saving any more images so don't care if haven't received new image.
                return "normal";
            }

            // Check if we should have a new image by now.
            // Add on 1 second to imageTriggerPeriod to account for the transmission time when the camera
            // is done processing, but the image data is being transferred.  
            // Actual 'handling' of image is done in event handler.
            if (!receivedNewImage && (TimeSinceLastTrigger > (imageTriggerPeriod+1)))
            {
                return "timed_out";
            }
            else
            {
                return "normal";
            }
        }

        protected void Trigger()
        {
            lastTriggerUtcTime = UtcTime;
            lastTriggerSysTime = SysTime;
            CameraHandler.TakePhoto();
        }

        protected override void HandleSpecialCommand(string command)
        {
            if (command.ToLower() == "trigger")
            {
                SendText("Manual trigger");
                Trigger();
            }
        }
    }
}
