using System;
using NUnit.Framework;
using OpenMetaverse;
using Gloebit.GloebitMoneyModule;

namespace OpenSim.Server.Handlers.Tests
{
    [TestFixture]
    public class GloebitSecurityTests
    {
        [Test]
        public void TransactionCallbackRequiresTheTransactionKey()
        {
            var transaction = new GloebitTransaction
            {
                TransactionID = UUID.Random().ToString(),
                CallbackKey = UUID.Random().ToString()
            };

            Assert.That(transaction.VerifyCallbackKey(transaction.CallbackKey), Is.True);
            Assert.That(transaction.VerifyCallbackKey(UUID.Random().ToString()), Is.False);
            Assert.That(transaction.VerifyCallbackKey(null), Is.False);
        }

        [Test]
        public void TransactionCallbackUrlsCarryTheirOwnKey()
        {
            var transaction = new GloebitTransaction
            {
                TransactionID = UUID.Random().ToString(),
                CallbackKey = UUID.Random().ToString()
            };
            var baseUri = new Uri("https://grid.example/gloebit/");

            foreach (Uri uri in new[]
            {
                transaction.BuildEnactURI(baseUri),
                transaction.BuildConsumeURI(baseUri),
                transaction.BuildCancelURI(baseUri)
            })
            {
                Assert.That(uri.Query, Does.Contain("key=" + Uri.EscapeDataString(transaction.CallbackKey)));
            }
        }
    }
}
