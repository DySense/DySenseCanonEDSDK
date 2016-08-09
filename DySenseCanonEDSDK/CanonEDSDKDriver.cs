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

        // System time stamp when last image was received.
        double lastReceivedSysTime = 0;

        // System time when a session was opened to the camera.
        double sessionOpenSysTime = 0;

        // System time that the camera was last attempted to be triggered at.
        double lastTryTriggerSysTime = 0;

        // System time that the camera was first attempted to be triggered at (reset for each session)
        double firstTryTriggerSysTime = 0;

        SDKHandler CameraHandler;

        // Serial number of camera to connect to.
        string serialNumber;

        double captureLatency;

        bool lowBatteryCheck = false;

        bool lowBattery = false;

        bool portInUse = false;

        bool unhandledCameraErrorOccurred = false;

        // Last image number received by camera.  Used to detect rollover from 9999 -> 1
        int lastCamImageNumber = 0;

        // How many times camera has rolled over numbering from 9999 -> 1.
        int numberOfTimesNumberingRolledOver = 0;

        // Set to true when we receive the first image from the camera.
        bool firstImageReceived = false;

        // Number of times that camera failed to trigger, that haven't been accounted for yet.
        int numUnhandledTriggerFailed = 0;

        // When we trigger a photo this is our image number we expect it to have.
        int nextExpectedTriggerNumber = -1;

        // When we receive a photo this is our image number we expect it to have.
        int nextExpectedReceivedNumber = -1;

        // List of images that have potentially been 'triggered', but not yet received.
        List<TriggerImageInfo> triggerImageInfo = new List<TriggerImageInfo>();

        // List of images that have been 'received', but not in the correct order so they're buffered until we 
        // receive the right one. 
        List<ReceivedImageInfo> bufferedImageInfo = new List<ReceivedImageInfo>();

        double TimeSinceLastTriggerAttempt { get { return SysTime - lastTryTriggerSysTime; } }

        double TimeSinceFirstTriggerAttempt { get { return SysTime - firstTryTriggerSysTime; } }

        double TimeSinceLastReceivedImage { get { return SysTime - lastReceivedSysTime; } }

        double TimeSessionHasBeenOpen { get { return SysTime - sessionOpenSysTime; } }
        
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

        // Reset any driver fields that change with a camera session back to their default states.
        private void ResetSessionFields()
        {
            lastReceivedSysTime = 0;
            sessionOpenSysTime = 0;
            lastTryTriggerSysTime = 0;
            firstTryTriggerSysTime = 0;
            lowBattery = false;
            portInUse = false;
            unhandledCameraErrorOccurred = false;
            lastCamImageNumber = 0;
            numberOfTimesNumberingRolledOver = 0;
            firstImageReceived = false;
            numUnhandledTriggerFailed = 0;
            nextExpectedTriggerNumber = -1;
            nextExpectedReceivedNumber = -1;
            triggerImageInfo.Clear();
            bufferedImageInfo.Clear(); // TODO clear this or flush it?
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

            // It's critical that any error code that causes the camera not to take a picture,
            // but also not fail is listed here.
            if (errorID == EDSDK.EDS_ERR_DEVICE_BUSY)
            {
                numUnhandledTriggerFailed++;
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

        void CameraHandler_NewImageDownloaded(string originalFilePath)
        {
            string imageType; // e.g. IMG or CAM1
            int camImageNumber; // e.g. 1 or 1852
            string imageExtension; // e.g. JPG or CR2
            bool parseSuccessful = ParseImageFilePath(originalFilePath, out imageType, out camImageNumber, out imageExtension);

            if (!parseSuccessful)
            {
                SendText(String.Format("Could not parse image \"{0}\"", Path.GetFileName(originalFilePath)));
                return;
            }

            lastReceivedSysTime = SysTime;

            // Detect if camera numbering rolled over from 9999 to 1.
            // Can't just check if new image number is less than last since don't always receive in order.
            // For example could go 9998 -> 1 -> 9999 -> 2 -> 3... and that would only be one rollover.
            if (firstImageReceived)
            {
                if (lastCamImageNumber > 9000 && camImageNumber < 1000)
                {
                    numberOfTimesNumberingRolledOver++;
                }
                else if (lastCamImageNumber < 1000 && camImageNumber > 9000)
                {
                    // Rare case of rolled over, but then got out of order, so need to undo rollover, which should then
                    // shortly be re-done in either the next image or the one after that.
                    numberOfTimesNumberingRolledOver--; 
                }   
            }

            // Save this so we know for next time.
            lastCamImageNumber = camImageNumber;

            // Convert image number into OUR numbering scheme (i.e. doesn't wrap at 9999)
            int imageNumber = camImageNumber + (numberOfTimesNumberingRolledOver * 9999);

            // Find matching number in trigger info so we know what time the picture was taken at.
            // Iterate backwards through list so we can safetly delete elements.
            // If we haven't received first image yet then we want to use the first triggerInfo and clear the entire list.
            TriggerImageInfo matchingTrigger = null;
            for (int i = triggerImageInfo.Count - 1; i >= 0; i--)
            {
                TriggerImageInfo trigger = triggerImageInfo[i];
                if (!firstImageReceived || (trigger.expectedImageNumber == imageNumber))
                {
                    matchingTrigger = trigger;
                    triggerImageInfo.RemoveAt(i);
                }
            }
            
            if (matchingTrigger == null)
            {
                SendText(String.Format("Could find matching trigger info for image number {0}", imageNumber));
                DontKnowWhatNumberToExpect();
                return;
            }

            // We have valid information for our newly downloaded image.
            ReceivedImageInfo receivedInfo = new ReceivedImageInfo(originalFilePath, matchingTrigger, imageType, imageNumber, camImageNumber, imageExtension);

            // Set a flag to determine we want to handle data (rename and send) right now or if we need to
            // wait because it's out of order.
            bool handleNewImageRightNow = true;

            if (!firstImageReceived)
            {
                firstImageReceived = true;
                nextExpectedReceivedNumber = imageNumber + 1;
                nextExpectedTriggerNumber = imageNumber + 1;
            }
            else // we know what the next number should be, so verify it.
            {
                if (matchingTrigger.expectedImageNumber < nextExpectedReceivedNumber)
                {
                    SendText(String.Format("Received image number {0} is lower than expected {1}. This should never happen.", imageNumber, nextExpectedReceivedNumber));
                    DontKnowWhatNumberToExpect();
                    // return; handle it anyways... it will just be out of order in the log, but still correct.
                }
                else if (matchingTrigger.expectedImageNumber > nextExpectedReceivedNumber)
                {
                    // Received image out of order.  This is normal.  Just buffer it to write it out later.
                    handleNewImageRightNow = false;
                    bufferedImageInfo.Add(receivedInfo);
                }
                else
                {
                    // We received the one we were expecting... so now we should start expecting the next one.s
                    nextExpectedReceivedNumber++;
                } 
            }

            if (handleNewImageRightNow)
            {
                RenameAndHandleNewImageInfo(receivedInfo);
                
                // Handle any other buffered elements that immediately follow this one.
                bool handledBufferImage = true;
                while (handledBufferImage)
                {
                    handledBufferImage = false; // Assume we won't find any matching buffer elements.
                    for (int i = bufferedImageInfo.Count - 1; i >= 0; i--)
                    {
                        ReceivedImageInfo info = bufferedImageInfo[i];
                        if (info.triggerInfo.expectedImageNumber == nextExpectedReceivedNumber)
                        {
                            //SendText("Flushing " + info.imageNumber);
                            RenameAndHandleNewImageInfo(info);
                            nextExpectedReceivedNumber++;
                            bufferedImageInfo.RemoveAt(i);
                            handledBufferImage = true; // so keep looking for next one.
                            break; // only try to match one each time through loop.
                        }
                    }
                }
            }
            else
            {
                // Make sure we haven't buffered up too many images.  This is a sign that the image we're expecting is
                // never going to come.  In that case just handle everything we have and start expecting the next one.
                if (bufferedImageInfo.Count > 5)
                {
                    List<ReceivedImageInfo> sortedBufferedImageInfo = bufferedImageInfo.OrderBy(o => o.triggerInfo.expectedImageNumber).ToList();
                    foreach (ReceivedImageInfo info in sortedBufferedImageInfo)
                    {
                        //SendText("Fail Flushing " + info.imageNumber);
                        RenameAndHandleNewImageInfo(info);
                    }
                    DontKnowWhatNumberToExpect();
                    bufferedImageInfo.Clear(); // since we just handled them all.
                }
            }
        }

        private void RenameAndHandleNewImageInfo(ReceivedImageInfo info)
        {
            // Format current (Unix) UTC time so we can use it when renaming file.
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dateTime = dateTime.AddSeconds(info.triggerInfo.utcTime);
            string formattedTime = dateTime.ToString("yyyyMMdd_hhmmss");

            string originalImageFilename = Path.GetFileName(info.originalFilePath);
            string newFileName = String.Format("{0}_{1}_{2}_{3}.{4}", InstrumentID, formattedTime, info.imageType, info.imageNumber, info.imageExtension);
            string newFilePath = Path.Combine(CurrentDataFileDirectory, newFileName);

            // Rename image.  If there's already an image with the same name then delete it first.
            if (File.Exists(newFilePath))
            {
                File.Delete(newFilePath);
            }
            File.Move(info.originalFilePath, newFilePath);

            HandleData(info.triggerInfo.utcTime, info.triggerInfo.sysTime, new List<object>() { newFileName, info.triggerInfo.sysTime, info.camImageNumber });
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

            ResetSessionFields();
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
                    sessionOpenSysTime = SysTime;

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
            if (this.lowBatteryCheck && lowBattery) { return; }

            // Handle if the last image failed to trigger the camera.  Need to do this here 
            // instead of in the HandleError callback so it's running on our thread.
            int errorsToHandle = numUnhandledTriggerFailed;
            for (int n = 0; n < errorsToHandle; n++)
            {
                // This essentially needs to 'undo' the last trigger so it was like it never happened.
                if (triggerImageInfo.Count > 0)
                {
                    //SendText("Removing " + triggerImageInfo[triggerImageInfo.Count - 1].expectedImageNumber.ToString());
                    triggerImageInfo.RemoveAt(triggerImageInfo.Count - 1);
                }
                nextExpectedTriggerNumber--;
                //SendText(String.Format("Exp. Trigger {0} -> {1}", nextExpectedTriggerNumber + 1, nextExpectedTriggerNumber));
            }
            numUnhandledTriggerFailed -= errorsToHandle;

            if (errorsToHandle > 0)
            {
                // Try to trigger again as soon as possible.
                Trigger();
                //SendText("(retrigger)");
                return;
            }

            // Don't trigger again while we're still waiting for the first image so we can
            // know what the rest of the image numbers should be coming back from the camera.
            // (since they don't always come in order)
            if (triggerImageInfo.Count > 0 && !firstImageReceived) { return; }

            // Wait until enough time has elapsed before triggering again.
            if (TimeSinceLastTriggerAttempt < imageTriggerPeriod) { return; }

            Trigger();
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

            // Special case when waiting for image to arrive.
            if (!firstImageReceived && (TimeSinceFirstTriggerAttempt > (imageTriggerPeriod + 5)))
            {
                return "timed_out";
            }

            // Check if we should have a new image by now.
            // Add on a couple seconds to imageTriggerPeriod to account for the transmission time when the camera
            // is done processing, but the image data is being transferred.  
            // Actual 'handling' of image is done in event handler.
            if (firstImageReceived && (TimeSinceLastReceivedImage > (imageTriggerPeriod + 3)))
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
            //if (nextExpectedTriggerNumber > 100)
            //{
            //    SendText("Skip Trigger...");
            //    return;
            //}

            // Add on latency to timestamps to account for the time before image is exposed.
            double triggerUtcTime = UtcTime + captureLatency;
            double triggerSysTime = SysTime + captureLatency;
            CameraHandler.TakePhoto();

            lastTryTriggerSysTime = triggerSysTime;

            if (firstTryTriggerSysTime == 0)
            {
                firstTryTriggerSysTime = triggerSysTime;
            }

            //SendText("TRIGGER " + nextExpectedTriggerNumber.ToString());

            // Save off trigger info in case the camera couldn't actually take a picture.
            triggerImageInfo.Add(new TriggerImageInfo(triggerUtcTime, triggerSysTime, nextExpectedTriggerNumber));
            nextExpectedTriggerNumber++;
        }

        protected override void Resume()
        {
            base.Resume();

            // Pretend like we just got an image to prevent unnecessary timeout after being paused. 
            lastReceivedSysTime = SysTime;
        }

        protected override void HandleSpecialCommand(string commandName, object commandArgs)
        {
            if (commandName == "trigger_once")
            {
                SendText("Manual trigger");
                Trigger();
            }
        }

        // Call this function when the numbering gets messed up and we just want to start over like we haven't
        // gotten an image yet and we don't know what number to expect.
        private void DontKnowWhatNumberToExpect()
        {
            firstImageReceived = false;
            if (triggerImageInfo.Count > 1)
            {
                // Clear everything but the last element so that's the one that gets matched next time.
                TriggerImageInfo mostRecentTrigger = triggerImageInfo[triggerImageInfo.Count - 1];
                triggerImageInfo.Clear();
                triggerImageInfo.Add(mostRecentTrigger);
            }
        }

        private bool ParseImageFilePath(string filePath, out string imageType, out int imageNumber, out string extension)
        {
            // Initialize output parameters.
            extension = Path.GetExtension(filePath).Replace(".", "");
            imageType = "";
            imageNumber = 0;

            string fileName = Path.GetFileNameWithoutExtension(filePath);

            string[] parts = fileName.Split(new char[] { '_', '-', ' ' });

            if (parts.Length < 2)
            {
                return false; // error, not enough parts to file name.
            }

            bool success = int.TryParse(parts[parts.Length - 1], out imageNumber);
            if (!success)
            {
                return false; // error, couldn't convert image number to an integer.
            }

            imageType = parts[parts.Length - 2];

            return true; // success
        }

        // Will return image number from 1 -> 9999.  
        // Examples
        //  10 -> 10
        //  10000 -> 1
        //  10005 -> 6
        //  0 -> 9999
        // -1 -> 9998
        private int WrapImageNumber(int imageNumber)
        {
            if (imageNumber > 9999)
            {
                imageNumber = (imageNumber % 10000) + 1;
            }
            else if (imageNumber < 1)
            {
                imageNumber += 9999;
            }

            return imageNumber; 
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

        # region ErrorMappingsToString

        Dictionary<uint, string> errorToString = new Dictionary<uint, string> 
        {
            { EDSDK.EDS_ERR_COMM_PORT_IS_IN_USE, "Port in use." },
            { EDSDK.EDS_ERR_COMM_DISCONNECTED, "Port disconnected." },
            { EDSDK.EDS_ERR_STREAM_OPEN_ERROR, "Cannot open camera stream." },
            { EDSDK.EDS_ERR_INVALID_HANDLE, "Invalid handle." },
        };

        #endregion
    }
}
