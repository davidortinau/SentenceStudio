using SQLite;

namespace SentenceStudio.Models;
    
public class UserProfile
{
    [PrimaryKey, AutoIncrement]
    public int ID { get; set; }

    public string Name { get; set; }
    public string NativeLanguage { get; set; } = "English";
    public string TargetLanguage { get; set; } = "Korean";
    public string DisplayLanguage { get; set; }
    public string Email { get; set; }
    public string OpenAI_APIKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public string DisplayCulture { 
        get{
            if(DisplayLanguage == "English")
            {
                return "en";
            }
            else if(DisplayLanguage == "Korean")
            {
                return "ko";
            }
            else
            {
                return "en";
            }
        }   
    }
}