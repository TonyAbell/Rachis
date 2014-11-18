namespace Rhino.Raft.Messages
{
    public class RequestVoteResponse : BaseMessage
    {
        public long CurrentTerm { get; set; }
		public long VoteTerm { get; set; }
        public bool VoteGranted { get; set; }
        public string Message { get; set; }
		public bool TrialOnly { get; set; }
    }
}