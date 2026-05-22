using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

/// <summary>
/// Single source of truth for the predefined <see cref="ConversationScenario"/> set.
/// Consumed by both the API (server-side seeding to Postgres on startup) and the
/// MAUI app (local SQLite seeding via <c>ScenarioService.SeedPredefinedScenariosAsync</c>).
/// Keep this list in sync — both seeders are idempotent and re-run safely.
/// </summary>
public static class ConversationScenarioSeedData
{
    /// <summary>
    /// Returns fresh instances of every predefined scenario.
    /// Returns new objects on each call so callers may mutate (e.g. set <c>CreatedAt</c>/<c>UpdatedAt</c>)
    /// without cross-talk between seeders.
    /// </summary>
    public static IReadOnlyList<ConversationScenario> GetPredefinedScenarios()
    {
        return new[]
        {
            new ConversationScenario
            {
                Name = "First Meeting",
                NameKorean = "첫 만남",
                PersonaName = "김철수",
                PersonaDescription = "a 25-year-old drama writer from Seoul",
                SituationDescription = "Getting acquainted with a new person",
                ConversationType = ConversationType.OpenEnded,
                QuestionBank = "몇 살이에요? 성함이 어떻게 되세요? 생일이 언제예요? 나이가 어떻게 되세요? 무슨 일해요? 어디에 살아요? 어릴 때 뭐가 되고 싶었어요? 취미가 뭐예요? 뭐 좋아해요? 취미가 어떻게 되세요? 왜 한국어 배워요? 오늘 뭐 먹었어요? 지난 주말에 뭐 했어요? 내일 뭐 할 거예요? 어느 나라 여행하고 싶어요? 한 주 동안 뭐 했어요? 한국에 가 봤어요? 한국에 가면 뭐 해 보고 싶어요?",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Ordering Coffee",
                NameKorean = "커피 주문",
                PersonaName = "박지영",
                PersonaDescription = "a friendly barista at a local cafe",
                SituationDescription = "Ordering coffee and snacks at a Korean cafe",
                ConversationType = ConversationType.Finite,
                QuestionBank = "",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Ordering Dinner",
                NameKorean = "저녁 식사 주문",
                PersonaName = "이민호",
                PersonaDescription = "a waiter at a Korean BBQ restaurant",
                SituationDescription = "Ordering food at a Korean BBQ restaurant",
                ConversationType = ConversationType.Finite,
                QuestionBank = "몇 분이세요? 뭐 드시겠어요? 고기는 어떤 거로 하시겠어요? 반찬 더 필요하세요? 음료는요? 디저트는요? 계산은 어떻게 하시겠어요?",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Asking for Directions",
                NameKorean = "길 찾기",
                PersonaName = "최수진",
                PersonaDescription = "a helpful stranger on the street",
                SituationDescription = "Asking for directions to a destination",
                ConversationType = ConversationType.Finite,
                QuestionBank = "어디 가세요? 이 근처 아세요? 지하철역이 어디예요? 버스 정류장이 어디예요? 얼마나 걸려요? 걸어서 갈 수 있어요?",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Weekend Plans",
                NameKorean = "주말 계획",
                PersonaName = "김하나",
                PersonaDescription = "a curious friend asking about your plans",
                SituationDescription = "Discussing weekend activities and plans with a friend",
                ConversationType = ConversationType.OpenEnded,
                QuestionBank = "주말에 뭐 해요? 어디 가요? 누구랑 가요? 뭐 먹을 거예요? 영화 볼 거예요? 쇼핑할 거예요? 집에서 쉴 거예요?",
                IsPredefined = true
            }
        };
    }
}
