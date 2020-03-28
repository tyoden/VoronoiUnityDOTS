namespace Voronoi.Structures
{
	public struct Arc
	{
		public readonly int Index;

		public readonly int Site;

		public int Edge;

		public FortuneEvent Event;

		public Arc(int index, int siteIndex)
		{
			Index = index;
			Site = siteIndex;
			Edge = -1;
			Event = new FortuneEvent();
		}
	}
}