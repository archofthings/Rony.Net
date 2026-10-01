using Rony.Net;
using System;
using Xunit;

namespace Rony.Tests.Verification
{
    public class TimesTests
    {
        [Theory]
        [InlineData(0, true, false, false)]
        [InlineData(1, false, true, true)]
        [InlineData(2, false, false, true)]
        public void Times_Should_Match_Counts(int count, bool never, bool once, bool atLeastOnce)
        {
            //Assert
            Assert.Equal(never, Times.Never().Matches(count));
            Assert.Equal(once, Times.Once().Matches(count));
            Assert.Equal(atLeastOnce, Times.AtLeastOnce().Matches(count));
        }

        [Fact]
        public void Times_Should_Support_Ranges()
        {
            //Assert
            Assert.True(Times.Exactly(3).Matches(3));
            Assert.False(Times.Exactly(3).Matches(2));
            Assert.True(Times.AtLeast(2).Matches(5));
            Assert.False(Times.AtMost(2).Matches(3));
            Assert.True(Times.Between(2, 4).Matches(4));
            Assert.False(Times.Between(2, 4).Matches(1));
        }

        [Fact]
        public void Times_Should_Describe_Itself()
        {
            //Assert
            Assert.Equal("never", Times.Never().ToString());
            Assert.Equal("exactly 1 time", Times.Once().ToString());
            Assert.Equal("exactly 3 times", Times.Exactly(3).ToString());
            Assert.Equal("at least 1 time", Times.AtLeastOnce().ToString());
            Assert.Equal("at most 2 times", Times.AtMost(2).ToString());
            Assert.Equal("between 2 and 4 times", Times.Between(2, 4).ToString());
        }

        [Fact]
        public void Times_Should_Reject_Invalid_Counts()
        {
            //Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => Times.Exactly(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Times.Between(3, 2));
        }
    }
}
