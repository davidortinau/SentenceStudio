
namespace SentenceStudio.Models;
public partial class StreamHistory : ObservableObject
{
    public string Phrase { get; set; }
    public Stream Stream { get; set; }    
}