using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Text;

namespace ComeBearingPresence.Func.Model
{
    public class UserPresence : TableEntity
    {
        public string ObjectId { get; set; }
        public string Upn { get; set; }
        public string Availability { get; set; }
        public string Activity { get; set; }
    }

}
