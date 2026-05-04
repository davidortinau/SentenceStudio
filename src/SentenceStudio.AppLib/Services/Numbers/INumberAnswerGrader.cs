namespace SentenceStudio.Services.Numbers;

public interface INumberAnswerGrader
{
    GradeResult Grade(NumberItem item, string userAnswer, int latencyMs);
}
