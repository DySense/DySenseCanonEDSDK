using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DySenseCanonEDSDK
{
    public class TriggerImageInfo
    {
        public double utcTime;
        public double sysTime;
        public int expectedImageNumber;

        public TriggerImageInfo(double utcTime, double sysTime, int expectedImageNumber)
        {
            this.utcTime = utcTime;
            this.sysTime = sysTime;
            this.expectedImageNumber = expectedImageNumber;
        }
    }

    public class ReceivedImageInfo
    {
        public string originalFilePath;
        public TriggerImageInfo triggerInfo;
        public string imageType;
        public int imageNumber;
        public int camImageNumber; // number from camera
        public string imageExtension;

        public ReceivedImageInfo(string originalFilePath, TriggerImageInfo triggerInfo, string imageType, int imageNumber, int camImageNumber, string imageExtension)
        {
            this.originalFilePath = originalFilePath;
            this.triggerInfo = triggerInfo;
            this.imageType = imageType;
            this.imageNumber = imageNumber;
            this.camImageNumber = camImageNumber;
            this.imageExtension = imageExtension;
        }

    }
}
