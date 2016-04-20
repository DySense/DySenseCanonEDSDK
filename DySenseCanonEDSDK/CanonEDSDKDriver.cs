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

        SDKHandler CameraHandler;

        // Serial number of camera to connect to.
        string serialNumber;

        double captureLatency;

        bool lowBatteryCheck = false;

        bool lowBattery = false;

        bool portInUse = false;

        bool receivedNewImage = true;

        bool unhandledCameraErrorOccurred = false;

        double TimeSinceLastTrigger { get { return SysTime - lastTriggerSysTime; } }

        public CanonEDSDKDriver(string sensorID, string instrumentID, Dictionary<string, object> settings, string connectEndpoint)
            : base(sensorID, instrumentID, connectEndpoint, decideTimeout: false)
        {
            this.defaultDataFileDirectory = Convert.ToString(settings["out_directory"]);
            this.imageTriggerPeriod = Convert.ToDouble(settings["trigger_period"]);
            this.serialNumber = Convert.ToString(settings["serial_number"]);
            this.captureLatency = Convert.ToDouble(settings["capture_latency"]) / 1000.0;
            this.lowBatteryCheck = Convert.ToBoolean(settings["battery_check"]);

            // The desired read period is set much faster than actual trigger period so we can constantly check for SDK events.
            base.DesiredReadPeriod = 0.1; // seconds
            base.MaxClosingTime = 4.0; // seconds
        }

        protected override void Setup()
        {
            if (!String.IsNullOrWhiteSpace(CurrentDataFileDirectory) && !Directory.Exists(CurrentDataFileDirectory))
            {
                Directory.CreateDirectory(CurrentDataFileDirectory);
            }

            CameraHandler = new SDKHandler();
            //CameraHandler.SDKObjectEvent += handler_SDKObjectEvent;
            CameraHandler.CameraAdded += new SDKHandler.CameraAddedHandler(SDK_CameraAdded);
            CameraHandler.CameraHasShutdown += SDK_CameraHasShutdown;
            CameraHandler.NewImageDownloaded += CameraHandler_NewImageDownloaded;
            CameraHandler.CameraError += CameraHandler_CameraError;

            // Tell the handler where to save the images.
            CameraHandler.ImageSaveDirectory = CurrentDataFileDirectory;

            TryOpenSession();
        }

        protected void CheckForEdsdkEvents()
        {
            EDSDK.EdsGetEvent();
            if (this.unhandledCameraErrorOccurred)
            {
                throw new Exception("Unhandled camera error.");
            }
        }

        // I think this is running from the EDSDK thread.
        void CameraHandler_CameraError(uint errorID)
        {
            string errorString = String.Format("0x{0}", errorID.ToString("X"));
            if (errorToString.ContainsKey(errorID))
            {
                errorString = errorToString[errorID];
            }

            // Port is already opened by another driver instance.
            if (errorID == EDSDK.EDS_ERR_COMM_PORT_IS_IN_USE)
            {
                this.portInUse = true;
            }
            
            if (recoverableErrors.ContainsKey(errorID))
            {
                bool printMessage = recoverableErrors[errorID];
                if (printMessage)
                {
                    SendText("Camera Error: " + errorString);
                }
            }
            else // can't recover or don't know if can recover from error.
            {
                SendText("Unhandled Camera Error: " + errorString);
                this.unhandledCameraErrorOccurred = true;
            }
        }

        void CameraHandler_NewImageDownloaded(string filePath)
        {
            // TODO rename and just log new filename.
            string imageFilename = Path.GetFileName(filePath);
            SendText(String.Format("Downloaded {0}", imageFilename));
            HandleData(lastTriggerUtcTime, lastTriggerSysTime, new List<object>() { imageFilename, lastTriggerSysTime });

            // Set flag so we know we can trigger again.
            receivedNewImage = true;
        }

        private void SDK_CameraAdded()
        {
            string cameraType = CameraHandler.MainCamera.Info.szDeviceDescription;
            SendText("Detected " + cameraType);
            TryOpenSession();
        }

        private void SDK_CameraHasShutdown(object sender, EventArgs e)
        {
            // TODO - check which camera closed??
            SendText("Camera has shutdown.");
            CameraHandler.CloseSession();
        }

        private void TryOpenSession()
        {
            if (CameraHandler.CameraSessionOpen)
            {
                return; // Camera session already opened.
            }

            foreach (Camera camera in CameraHandler.GetCameraList())
            {
                // Have to open a session to query the body ID (ie the serial number) to check
                // if it matches the one we're looking for.
                CameraHandler.OpenSession(camera);
                string bodyID = CameraHandler.GetStringSetting(EDSDK.PropID_BodyIDEx);

                // Make sure another driver instance isn't already communicating with camera.
                CheckForEdsdkEvents();
                if (this.portInUse)
                {
                    this.portInUse = false; // reset flag for next camera.
                    CameraHandler.CloseSession();
                    continue; // check if next camera doesn't have a session open yet.
                }

                if (bodyID == this.serialNumber)
                {
                    SendText("Found matching serial number. Camera connected.");

                    if (CameraHandler.GetSetting(EDSDK.PropID_AEMode) != EDSDK.AEMode_Manual)
                    {
                        SendText("Camera is not in manual mode. Some features might not work!");
                    }

                    uint batteryLevel = CameraHandler.GetSetting(EDSDK.PropID_BatteryLevel);

                    this.lowBattery = (batteryLevel == EDSDK.BatteryLevel_Low) || (batteryLevel == EDSDK.BatteryLevel_Empty);

                    // Tell the camera to send back images instead of saving to card.
                    CameraHandler.SetSetting(EDSDK.PropID_SaveTo, (uint)EDSDK.EdsSaveTo.Host);
                    CameraHandler.SetCapacity();

                    CheckForEdsdkEvents();

                    break; // from camera loop since found right sensor
                }
                else
                {
                    SendText(String.Format("Not connecting to camera because serial number ({0}) doesn't match driver.", bodyID));
                    CameraHandler.CloseSession();
                }
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

            // Make sure battery isn't too low.
            if (this.lowBatteryCheck && lowBattery) { return;  }

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

            if (this.lowBatteryCheck && lowBattery)
            {
                return "low_battery";
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

        protected override void DriverHandleNewSetting(string settingName, object settingValue)
        {
            if (settingName == "data_file_directory")
            {
                // Data file directory might have changed so make sure directory exists.
                if (!String.IsNullOrWhiteSpace(CurrentDataFileDirectory) && !Directory.Exists(CurrentDataFileDirectory))
                {
                    Directory.CreateDirectory(CurrentDataFileDirectory);
                }

                if (CameraHandler != null)
                {
                    CameraHandler.ImageSaveDirectory = CurrentDataFileDirectory;
                }
            }
        }

        protected void Trigger()
        {
            // Add on latency to timestamps to account for the time before image is exposed.
            lastTriggerUtcTime = UtcTime + captureLatency;
            lastTriggerSysTime = SysTime + captureLatency;
            CameraHandler.TakePhoto();
        }

        protected override void HandleSpecialCommand(string command)
        {
            if (command.ToLower() == "trigger_once")
            {
                SendText("Manual trigger");
                Trigger();
            }
        }

        # region ErrorMappings

        // Value is whether or not to show an error message when error occurs.
        Dictionary<uint, bool> recoverableErrors = new Dictionary<uint, bool>
        {
            { EDSDK.EDS_ERR_DEVICE_BUSY, false },
            { EDSDK.EDS_ERR_COMM_PORT_IS_IN_USE, false },
            { EDSDK.EDS_ERR_COMM_DISCONNECTED, false },
        };

        #endregion

        # region ErrorMappings

        Dictionary<uint, string> errorToString = new Dictionary<uint, string> 
        {
            { EDSDK.EDS_ERR_COMM_PORT_IS_IN_USE, "Port in use." },
            { EDSDK.EDS_ERR_COMM_DISCONNECTED, "Port disconnected." },
        };

        #endregion
    }
}
