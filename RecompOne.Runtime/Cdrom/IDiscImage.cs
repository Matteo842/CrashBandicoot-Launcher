namespace RecompOne.Runtime.Cdrom;

internal interface IDiscImage : IDisposable
{
    long DataTrackBytes { get; }
    byte[] ReadSector(int lba);
    byte[] ReadSectorData(int lba, int size);
}
