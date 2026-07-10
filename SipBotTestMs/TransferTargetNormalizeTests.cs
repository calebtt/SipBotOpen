using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SipBot.Tests;

[TestClass]
public class TransferTargetNormalizeTests
{
    [DataRow("sip:102@slowcasting.com", "slowcasting.com", "sip:102@slowcasting.com")]
    [DataRow("102@slowcasting.com", "slowcasting.com", "sip:102@slowcasting.com")]
    [DataRow("102", "slowcasting.com", "sip:102@slowcasting.com")]
    [DataRow(" 102 ", "slowcasting.com", "sip:102@slowcasting.com")]
    [DataRow("102", "sip:slowcasting.com", "sip:102@slowcasting.com")]
    [DataTestMethod]
    public void NormalizeTransferTarget_AcceptsCommonForms(string input, string server, string expected)
    {
        Assert.AreEqual(expected, StreamingVoiceSipBotClient.NormalizeTransferTarget(input, server));
    }
}
