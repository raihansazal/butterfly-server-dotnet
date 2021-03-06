﻿/* Any copyright is dedicated to the Public Domain.
 * http://creativecommons.org/publicdomain/zero/1.0/ */

using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Butterfly.Core.Notify.Test;

namespace Butterfly.Aws.Test {
    [TestClass]
    public class AwsTest {
        [TestMethod]
        public async Task SendAwsSesNotifyMessage() {
            var notifyMessageSender = new AwsSesEmailNotifyMessageSender();
            await NotifyTest.SendEmailNotifyMessage(notifyMessageSender);
        }
    }
}
