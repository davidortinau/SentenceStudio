#!/usr/bin/env python3
"""
Import legacy SentenceStudio backup (INTEGER PKs) into current DB (GUID PKs).

Usage:
    python3 scripts/import_backup.py --dry-run          # preview only
    python3 scripts/import_backup.py --execute           # do the import
    python3 scripts/import_backup.py --execute --backup ~/Library/sstudio.bk.db3

The script:
  1. Backs up the target DB before any writes
  2. Maps old INTEGER PKs to new TEXT GUIDs
  3. Deduplicates by natural keys (vocab term, resource title, etc.)
  4. Remaps all FKs in dependency order
  5. Runs inside a single transaction (all-or-nothing)
  6. Validates FK integrity after import
"""

import argparse
import os
import shutil
import sqlite3
import sys
import uuid
from collections import defaultdict
from datetime import datetime, timezone


def guid():
    return str(uuid.uuid4())


def connect(path):
    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    return conn


def get_trigger_names(conn):
    rows = conn.execute("SELECT name FROM sqlite_master WHERE type='trigger'").fetchall()
    return [r["name"] for r in rows]


def disable_triggers(conn):
    triggers = get_trigger_names(conn)
    sqls = []
    for name in triggers:
        ddl = conn.execute(
            "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=?", (name,)
        ).fetchone()
        if ddl:
            sqls.append(ddl["sql"])
        conn.execute(f'DROP TRIGGER IF EXISTS "{name}"')
    return sqls  # save DDL for restore


def restore_triggers(conn, trigger_sqls):
    for sql in trigger_sqls:
        if sql:
            conn.execute(sql)


# ---------------------------------------------------------------------------
# Import logic
# ---------------------------------------------------------------------------

class Importer:
    def __init__(self, backup_path, target_path, dry_run=True):
        self.backup_path = backup_path
        self.target_path = target_path
        self.dry_run = dry_run

        # ID maps: old_int → new_guid (or existing guid)
        self.user_map = {}
        self.skill_map = {}
        self.resource_map = {}
        self.word_map = {}
        self.progress_map = {}
        self.challenge_map = {}
        self.conversation_map = {}
        self.chunk_map = {}
        self.vocablist_map = {}
        self.learningctx_map = {}
        self.scenario_map = {}  # int → int (same type, but IDs may shift)
        self.mapping_map = {}   # ResourceVocabularyMapping

        # Stats
        self.stats = defaultdict(lambda: {"inserted": 0, "skipped": 0, "merged": 0})

    def run(self):
        print(f"{'DRY RUN' if self.dry_run else 'EXECUTING'} import")
        print(f"  Backup: {self.backup_path}")
        print(f"  Target: {self.target_path}")
        print()

        if not os.path.exists(self.backup_path):
            print(f"ERROR: Backup file not found: {self.backup_path}")
            return False

        # Pre-flight backup
        if not self.dry_run:
            ts = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
            backup_copy = f"{self.target_path}.pre-import-{ts}.bak"
            shutil.copy2(self.target_path, backup_copy)
            print(f"  Safety backup: {backup_copy}")
            print()

        self.bk = connect(self.backup_path)
        self.tgt = connect(self.target_path)

        # Disable triggers to avoid CoreSync CT noise
        trigger_sqls = []
        if not self.dry_run:
            trigger_sqls = disable_triggers(self.tgt)
            self.tgt.execute("PRAGMA foreign_keys=OFF")
            self.tgt.execute("BEGIN")

        try:
            self._import_user_profile()
            self._import_conversation_scenarios()
            self._import_scene_images()
            self._import_skill_profiles()
            self._import_learning_resources()
            self._import_vocabulary_words()
            self._import_vocabulary_lists()
            self._import_resource_vocabulary_mappings()
            self._import_vocabulary_progress()
            self._import_vocabulary_learning_contexts()
            self._import_example_sentences()
            self._import_challenges()
            self._import_grade_responses()
            self._import_conversations()
            self._import_conversation_chunks()
            self._import_conversation_memory_states()
            self._import_user_activities()
            self._import_daily_plan_completions()
            self._import_stream_history()
            self._import_minimal_pairs()
            self._import_minimal_pair_sessions()
            self._import_minimal_pair_attempts()

            if not self.dry_run:
                self.tgt.execute("COMMIT")
                print("\nTransaction committed.")
        except Exception as e:
            if not self.dry_run:
                self.tgt.execute("ROLLBACK")
                print(f"\nROLLBACK due to error: {e}")
            raise
        finally:
            if not self.dry_run:
                restore_triggers(self.tgt, trigger_sqls)
                self.tgt.execute("PRAGMA foreign_keys=ON")

        # Validation
        if not self.dry_run:
            self._validate()

        self._print_summary()
        self.bk.close()
        self.tgt.close()
        return True

    # --- User Profile ---
    def _import_user_profile(self):
        print("1. UserProfile")
        bk_user = self.bk.execute("SELECT * FROM UserProfile").fetchone()
        # Map backup user 1 to current David profile
        target_david = self.tgt.execute(
            "SELECT Id FROM UserProfile WHERE Name LIKE '%David%' AND TargetLanguage='Korean'"
        ).fetchone()

        if target_david:
            self.user_map[bk_user["Id"]] = target_david["Id"]
            print(f"   Mapped backup user {bk_user['Id']} ('{bk_user['Name']}') → {target_david['Id']}")
            self.stats["UserProfile"]["skipped"] += 1
        else:
            new_id = guid()
            self.user_map[bk_user["Id"]] = new_id
            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO UserProfile (Id, Name, NativeLanguage, TargetLanguage, 
                       TargetLanguages, DisplayLanguage, Email, OpenAI_APIKey, 
                       PreferredSessionMinutes, TargetCEFRLevel, CreatedAt)
                       VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
                    (new_id, bk_user["Name"], bk_user["NativeLanguage"],
                     bk_user["TargetLanguage"], bk_user["TargetLanguages"],
                     bk_user["DisplayLanguage"], bk_user["Email"],
                     bk_user["OpenAI_APIKey"], bk_user["PreferredSessionMinutes"],
                     bk_user["TargetCEFRLevel"], bk_user["CreatedAt"]))
            self.stats["UserProfile"]["inserted"] += 1
            print(f"   Created new profile for '{bk_user['Name']}' → {new_id}")

    # --- ConversationScenario (int → int, same schema) ---
    def _import_conversation_scenarios(self):
        print("2. ConversationScenario")
        bk_rows = self.bk.execute("SELECT * FROM ConversationScenario").fetchall()
        existing = {r["Name"]: r["Id"] for r in
                    self.tgt.execute("SELECT Id, Name FROM ConversationScenario").fetchall()}

        for row in bk_rows:
            if row["Name"] in existing:
                self.scenario_map[row["Id"]] = existing[row["Name"]]
                self.stats["ConversationScenario"]["skipped"] += 1
            else:
                # Use same int ID if not taken, otherwise let autoincrement
                taken = set(r["Id"] for r in self.tgt.execute("SELECT Id FROM ConversationScenario").fetchall())
                new_id = row["Id"] if row["Id"] not in taken else None
                if not self.dry_run:
                    if new_id:
                        self.tgt.execute(
                            """INSERT INTO ConversationScenario 
                               (Id, Name, NameKorean, PersonaName, PersonaDescription, 
                                SituationDescription, ConversationType, QuestionBank,
                                IsPredefined, CreatedAt, UpdatedAt)
                               VALUES (?,?,?,?,?,?,?,?,?,?,?)""",
                            (new_id, row["Name"], row["NameKorean"], row["PersonaName"],
                             row["PersonaDescription"], row["SituationDescription"],
                             row["ConversationType"], row["QuestionBank"],
                             row["IsPredefined"], row["CreatedAt"], row["UpdatedAt"]))
                    else:
                        cur = self.tgt.execute(
                            """INSERT INTO ConversationScenario 
                               (Name, NameKorean, PersonaName, PersonaDescription,
                                SituationDescription, ConversationType, QuestionBank,
                                IsPredefined, CreatedAt, UpdatedAt)
                               VALUES (?,?,?,?,?,?,?,?,?,?)""",
                            (row["Name"], row["NameKorean"], row["PersonaName"],
                             row["PersonaDescription"], row["SituationDescription"],
                             row["ConversationType"], row["QuestionBank"],
                             row["IsPredefined"], row["CreatedAt"], row["UpdatedAt"]))
                        new_id = cur.lastrowid
                else:
                    new_id = new_id or (max(taken) + 1 if taken else row["Id"])
                self.scenario_map[row["Id"]] = new_id
                self.stats["ConversationScenario"]["inserted"] += 1
        # Also map None→None for conversations with no scenario
        self.scenario_map[None] = None
        print(f"   {self.stats['ConversationScenario']}")

    # --- SceneImage (int → int, same schema) ---
    def _import_scene_images(self):
        print("3. SceneImage")
        bk_rows = self.bk.execute("SELECT * FROM SceneImage").fetchall()
        existing_urls = {r["Url"] for r in
                        self.tgt.execute("SELECT Url FROM SceneImage").fetchall()}

        for row in bk_rows:
            if row["Url"] in existing_urls:
                self.stats["SceneImage"]["skipped"] += 1
            else:
                if not self.dry_run:
                    self.tgt.execute(
                        "INSERT INTO SceneImage (Url, Description, IsSelected) VALUES (?,?,?)",
                        (row["Url"], row["Description"], row["IsSelected"]))
                self.stats["SceneImage"]["inserted"] += 1
        print(f"   {self.stats['SceneImage']}")

    # --- SkillProfile ---
    def _import_skill_profiles(self):
        print("4. SkillProfile")
        bk_rows = self.bk.execute("SELECT * FROM SkillProfile").fetchall()
        existing = {}
        for r in self.tgt.execute("SELECT Id, Title, Language FROM SkillProfile").fetchall():
            existing[(r["Title"], r["Language"])] = r["Id"]

        user_guid = self.user_map.get(1, "")
        for row in bk_rows:
            key = (row["Title"], row["Language"])
            if key in existing:
                self.skill_map[row["Id"]] = existing[key]
                self.stats["SkillProfile"]["skipped"] += 1
            else:
                new_id = guid()
                self.skill_map[row["Id"]] = new_id
                if not self.dry_run:
                    self.tgt.execute(
                        """INSERT INTO SkillProfile 
                           (Id, Title, Description, Language, UserProfileId, CreatedAt, UpdatedAt)
                           VALUES (?,?,?,?,?,?,?)""",
                        (new_id, row["Title"], row["Description"], row["Language"],
                         user_guid, row["CreatedAt"], row["UpdatedAt"]))
                self.stats["SkillProfile"]["inserted"] += 1
        self.skill_map[None] = None
        print(f"   {self.stats['SkillProfile']}")

    # --- LearningResource ---
    def _import_learning_resources(self):
        print("5. LearningResource")
        bk_rows = self.bk.execute("SELECT * FROM LearningResource").fetchall()
        existing = {}
        for r in self.tgt.execute("SELECT Id, Title FROM LearningResource").fetchall():
            if r["Title"]:
                existing[r["Title"]] = r["Id"]

        user_guid = self.user_map.get(1, "")
        for row in bk_rows:
            if row["Title"] and row["Title"] in existing:
                self.resource_map[row["Id"]] = existing[row["Title"]]
                self.stats["LearningResource"]["skipped"] += 1
            else:
                new_id = guid()
                self.resource_map[row["Id"]] = new_id
                skill_id = self.skill_map.get(row["SkillID"])
                if not self.dry_run:
                    self.tgt.execute(
                        """INSERT INTO LearningResource 
                           (Id, SkillID, OldVocabularyListID, IsSmartResource, CreatedAt, UpdatedAt,
                            UserProfileId, Title, Description, MediaType, MediaUrl, Transcript,
                            Translation, Language, Tags, SmartResourceType)
                           VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                        (new_id, skill_id,
                         str(row["OldVocabularyListID"]) if row["OldVocabularyListID"] else None,
                         row["IsSmartResource"], row["CreatedAt"], row["UpdatedAt"],
                         user_guid, row["Title"], row["Description"],
                         row["MediaType"], row["MediaUrl"], row["Transcript"],
                         row["Translation"], row["Language"], row["Tags"],
                         row["SmartResourceType"]))
                self.stats["LearningResource"]["inserted"] += 1
        self.resource_map[None] = None
        print(f"   {self.stats['LearningResource']}")

    # --- VocabularyWord ---
    def _import_vocabulary_words(self):
        print("6. VocabularyWord")
        bk_rows = self.bk.execute("SELECT * FROM VocabularyWord").fetchall()

        # Build lookup of existing words by TargetLanguageTerm
        existing = {}
        for r in self.tgt.execute("SELECT Id, TargetLanguageTerm FROM VocabularyWord").fetchall():
            if r["TargetLanguageTerm"]:
                term = r["TargetLanguageTerm"].strip()
                existing[term] = r["Id"]

        for row in bk_rows:
            term = (row["TargetLanguageTerm"] or "").strip()
            if term and term in existing:
                self.word_map[row["Id"]] = existing[term]
                self.stats["VocabularyWord"]["skipped"] += 1
            else:
                new_id = guid()
                self.word_map[row["Id"]] = new_id
                if not self.dry_run:
                    self.tgt.execute(
                        """INSERT INTO VocabularyWord 
                           (Id, CreatedAt, UpdatedAt, NativeLanguageTerm, TargetLanguageTerm,
                            Lemma, Tags, MnemonicText, MnemonicImageUri, AudioPronunciationUri)
                           VALUES (?,?,?,?,?,?,?,?,?,?)""",
                        (new_id, row["CreatedAt"], row["UpdatedAt"],
                         row["NativeLanguageTerm"], row["TargetLanguageTerm"],
                         row["Lemma"], row["Tags"], row["MnemonicText"],
                         row["MnemonicImageUri"], row["AudioPronunciationUri"]))
                self.stats["VocabularyWord"]["inserted"] += 1
        self.word_map[None] = None
        print(f"   {self.stats['VocabularyWord']}")

    # --- VocabularyList ---
    def _import_vocabulary_lists(self):
        print("7. VocabularyList")
        bk_rows = self.bk.execute("SELECT * FROM VocabularyList").fetchall()
        existing_names = {r["Name"] for r in
                         self.tgt.execute("SELECT Name FROM VocabularyList").fetchall()}

        for row in bk_rows:
            if row["Name"] in existing_names:
                # find existing id
                eid = self.tgt.execute(
                    "SELECT Id FROM VocabularyList WHERE Name=?", (row["Name"],)
                ).fetchone()
                self.vocablist_map[row["Id"]] = eid["Id"] if eid else guid()
                self.stats["VocabularyList"]["skipped"] += 1
            else:
                new_id = guid()
                self.vocablist_map[row["Id"]] = new_id
                if not self.dry_run:
                    self.tgt.execute(
                        "INSERT INTO VocabularyList (Id, Name, CreatedAt, UpdatedAt) VALUES (?,?,?,?)",
                        (new_id, row["Name"], row["CreatedAt"], row["UpdatedAt"]))
                self.stats["VocabularyList"]["inserted"] += 1
        self.vocablist_map[None] = None
        print(f"   {self.stats['VocabularyList']}")

    # --- ResourceVocabularyMapping ---
    def _import_resource_vocabulary_mappings(self):
        print("8. ResourceVocabularyMapping")
        bk_rows = self.bk.execute(
            "SELECT * FROM ResourceVocabularyMapping"
        ).fetchall()

        # Build set of existing (ResourceId, VocabularyWordId) combos
        existing = set()
        for r in self.tgt.execute(
            "SELECT ResourceId, VocabularyWordId FROM ResourceVocabularyMapping"
        ).fetchall():
            existing.add((r["ResourceId"], r["VocabularyWordId"]))

        for row in bk_rows:
            # Column names differ: backup uses ResourceID/VocabularyWordID
            old_res = row["ResourceID"]
            old_word = row["VocabularyWordID"]
            new_res = self.resource_map.get(old_res)
            new_word = self.word_map.get(old_word)

            if not new_res or not new_word:
                self.stats["ResourceVocabularyMapping"]["skipped"] += 1
                continue

            if (new_res, new_word) in existing:
                self.stats["ResourceVocabularyMapping"]["skipped"] += 1
                continue

            new_id = guid()
            existing.add((new_res, new_word))
            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO ResourceVocabularyMapping (Id, ResourceId, VocabularyWordId)
                       VALUES (?,?,?)""",
                    (new_id, new_res, new_word))
            self.stats["ResourceVocabularyMapping"]["inserted"] += 1
        print(f"   {self.stats['ResourceVocabularyMapping']}")

    # --- VocabularyProgress ---
    def _import_vocabulary_progress(self):
        print("9. VocabularyProgress")
        bk_rows = self.bk.execute("SELECT * FROM VocabularyProgress").fetchall()
        user_guid = self.user_map.get(1, "")

        # Build existing lookup by (VocabularyWordId, UserId)
        existing = {}
        for r in self.tgt.execute(
            "SELECT * FROM VocabularyProgress"
        ).fetchall():
            existing[(r["VocabularyWordId"], r["UserId"])] = dict(r)

        for row in bk_rows:
            new_word_id = self.word_map.get(row["VocabularyWordId"])
            if not new_word_id:
                self.stats["VocabularyProgress"]["skipped"] += 1
                continue

            key = (new_word_id, user_guid)
            legacy_key = (new_word_id, "1")
            
            # Handle legacy UserId="1" records
            has_legacy = legacy_key in existing
            has_guid = key in existing
            
            if has_legacy and has_guid:
                # Both exist — merge legacy into GUID record, delete legacy
                legacy_rec = existing[legacy_key]
                guid_rec = existing[key]
                # Keep the one with more attempts as the canonical record
                best = legacy_rec if legacy_rec["TotalAttempts"] > guid_rec["TotalAttempts"] else guid_rec
                self.progress_map[row["Id"]] = guid_rec["Id"]
                if not self.dry_run:
                    if best is legacy_rec:
                        # Copy legacy data into GUID record
                        self.tgt.execute(
                            """UPDATE VocabularyProgress SET
                               MasteryScore=?, TotalAttempts=?, CorrectAttempts=?,
                               CurrentStreak=?, ProductionInStreak=?,
                               RecognitionAttempts=?, RecognitionCorrect=?,
                               ProductionAttempts=?, ProductionCorrect=?,
                               ApplicationAttempts=?, ApplicationCorrect=?,
                               CurrentPhase=?, NextReviewDate=?, ReviewInterval=?,
                               EaseFactor=?, MultipleChoiceCorrect=?, TextEntryCorrect=?,
                               IsPromoted=?, IsCompleted=?, FirstSeenAt=?,
                               LastPracticedAt=?, MasteredAt=?, UpdatedAt=?
                               WHERE Id=?""",
                            (best["MasteryScore"], best["TotalAttempts"],
                             best["CorrectAttempts"], best["CurrentStreak"],
                             best["ProductionInStreak"],
                             best["RecognitionAttempts"], best["RecognitionCorrect"],
                             best["ProductionAttempts"], best["ProductionCorrect"],
                             best["ApplicationAttempts"], best["ApplicationCorrect"],
                             best["CurrentPhase"], best["NextReviewDate"],
                             best["ReviewInterval"], best["EaseFactor"],
                             best["MultipleChoiceCorrect"], best["TextEntryCorrect"],
                             best["IsPromoted"], best["IsCompleted"],
                             best["FirstSeenAt"], best["LastPracticedAt"],
                             best["MasteredAt"], best["UpdatedAt"],
                             guid_rec["Id"]))
                    self.tgt.execute(
                        "DELETE FROM VocabularyProgress WHERE Id=?",
                        (legacy_rec["Id"],))
                del existing[legacy_key]
                self.stats["VocabularyProgress"]["merged"] += 1
            elif has_legacy and not has_guid:
                # Only legacy exists — reassign UserId to GUID
                ex = existing[legacy_key]
                self.progress_map[row["Id"]] = ex["Id"]
                if not self.dry_run:
                    self.tgt.execute(
                        "UPDATE VocabularyProgress SET UserId=? WHERE Id=?",
                        (user_guid, ex["Id"]))
                existing[key] = ex
                del existing[legacy_key]
                self.stats["VocabularyProgress"]["merged"] += 1
            
            if key in existing:
                # Merge: if backup has more attempts, update
                ex = existing[key]
                if row["TotalAttempts"] > ex["TotalAttempts"]:
                    self.progress_map[row["Id"]] = ex["Id"]
                    if not self.dry_run:
                        self.tgt.execute(
                            """UPDATE VocabularyProgress SET
                               MasteryScore=?, TotalAttempts=?, CorrectAttempts=?,
                               CurrentStreak=?, ProductionInStreak=?,
                               RecognitionAttempts=?, RecognitionCorrect=?,
                               ProductionAttempts=?, ProductionCorrect=?,
                               ApplicationAttempts=?, ApplicationCorrect=?,
                               CurrentPhase=?, NextReviewDate=?, ReviewInterval=?,
                               EaseFactor=?, MultipleChoiceCorrect=?, TextEntryCorrect=?,
                               IsPromoted=?, IsCompleted=?, FirstSeenAt=?,
                               LastPracticedAt=?, MasteredAt=?, UpdatedAt=?
                               WHERE Id=?""",
                            (row["MasteryScore"], row["TotalAttempts"],
                             row["CorrectAttempts"], row["CurrentStreak"],
                             row["ProductionInStreak"],
                             row["RecognitionAttempts"], row["RecognitionCorrect"],
                             row["ProductionAttempts"], row["ProductionCorrect"],
                             row["ApplicationAttempts"], row["ApplicationCorrect"],
                             row["CurrentPhase"], row["NextReviewDate"],
                             row["ReviewInterval"], row["EaseFactor"],
                             row["MultipleChoiceCorrect"], row["TextEntryCorrect"],
                             row["IsPromoted"], row["IsCompleted"],
                             row["FirstSeenAt"], row["LastPracticedAt"],
                             row["MasteredAt"], row["UpdatedAt"],
                             ex["Id"]))
                    self.stats["VocabularyProgress"]["merged"] += 1
                else:
                    self.progress_map[row["Id"]] = ex["Id"]
                    self.stats["VocabularyProgress"]["skipped"] += 1
            else:
                new_id = guid()
                self.progress_map[row["Id"]] = new_id
                if not self.dry_run:
                    self.tgt.execute(
                        """INSERT INTO VocabularyProgress 
                           (Id, VocabularyWordId, UserId, MasteryScore, TotalAttempts,
                            CorrectAttempts, CurrentStreak, ProductionInStreak,
                            RecognitionAttempts, RecognitionCorrect,
                            ProductionAttempts, ProductionCorrect,
                            ApplicationAttempts, ApplicationCorrect,
                            CurrentPhase, NextReviewDate, ReviewInterval, EaseFactor,
                            MultipleChoiceCorrect, TextEntryCorrect,
                            IsPromoted, IsCompleted, FirstSeenAt, LastPracticedAt,
                            MasteredAt, CreatedAt, UpdatedAt)
                           VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                        (new_id, new_word_id, user_guid,
                         row["MasteryScore"], row["TotalAttempts"],
                         row["CorrectAttempts"], row["CurrentStreak"],
                         row["ProductionInStreak"],
                         row["RecognitionAttempts"], row["RecognitionCorrect"],
                         row["ProductionAttempts"], row["ProductionCorrect"],
                         row["ApplicationAttempts"], row["ApplicationCorrect"],
                         row["CurrentPhase"], row["NextReviewDate"],
                         row["ReviewInterval"], row["EaseFactor"],
                         row["MultipleChoiceCorrect"], row["TextEntryCorrect"],
                         row["IsPromoted"], row["IsCompleted"],
                         row["FirstSeenAt"], row["LastPracticedAt"],
                         row["MasteredAt"], row["CreatedAt"], row["UpdatedAt"]))
                # Track the insert so duplicate backup words merge properly
                existing[key] = {"Id": new_id, "TotalAttempts": row["TotalAttempts"]}
                self.stats["VocabularyProgress"]["inserted"] += 1
        self.progress_map[None] = None
        print(f"   {self.stats['VocabularyProgress']}")

    # --- VocabularyLearningContext ---
    def _import_vocabulary_learning_contexts(self):
        print("10. VocabularyLearningContext")
        bk_rows = self.bk.execute("SELECT * FROM VocabularyLearningContext").fetchall()

        for row in bk_rows:
            new_progress_id = self.progress_map.get(row["VocabularyProgressId"])
            new_resource_id = self.resource_map.get(row["LearningResourceId"])

            if not new_progress_id:
                self.stats["VocabularyLearningContext"]["skipped"] += 1
                continue

            new_id = guid()
            self.learningctx_map[row["Id"]] = new_id
            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO VocabularyLearningContext 
                       (Id, VocabularyProgressId, LearningResourceId, Activity, InputMode,
                        WasCorrect, DifficultyScore, ResponseTimeMs, UserConfidence,
                        ContextType, UserInput, ExpectedAnswer,
                        LearnedAt, CorrectAnswersInContext, CreatedAt, UpdatedAt)
                       VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                    (new_id, new_progress_id, new_resource_id,
                     row["Activity"], row["InputMode"],
                     row["WasCorrect"], row["DifficultyScore"],
                     row["ResponseTimeMs"], row["UserConfidence"],
                     row["ContextType"], row["UserInput"], row["ExpectedAnswer"],
                     row["LearnedAt"], row["CorrectAnswersInContext"],
                     row["CreatedAt"], row["UpdatedAt"]))
            self.stats["VocabularyLearningContext"]["inserted"] += 1
        print(f"   {self.stats['VocabularyLearningContext']}")

    # --- ExampleSentence ---
    def _import_example_sentences(self):
        print("11. ExampleSentence")
        bk_rows = self.bk.execute("SELECT * FROM ExampleSentence").fetchall()

        for row in bk_rows:
            new_word_id = self.word_map.get(row["VocabularyWordId"])
            new_resource_id = self.resource_map.get(row["LearningResourceId"])

            if not new_word_id:
                self.stats["ExampleSentence"]["skipped"] += 1
                continue

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO ExampleSentence 
                       (VocabularyWordId, LearningResourceId, TargetSentence,
                        NativeSentence, AudioUri, IsCore, CreatedAt, UpdatedAt)
                       VALUES (?,?,?,?,?,?,?,?)""",
                    (new_word_id, new_resource_id,
                     row["TargetSentence"], row["NativeSentence"],
                     row["AudioUri"], row["IsCore"],
                     row["CreatedAt"], row["UpdatedAt"]))
            self.stats["ExampleSentence"]["inserted"] += 1
        print(f"   {self.stats['ExampleSentence']}")

    # --- Challenge ---
    def _import_challenges(self):
        print("12. Challenge")
        bk_rows = self.bk.execute("SELECT * FROM Challenge").fetchall()

        for row in bk_rows:
            new_id = guid()
            self.challenge_map[row["Id"]] = new_id
            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO Challenge 
                       (Id, CreatedAt, UpdatedAt, SentenceText, RecommendedTranslation,
                        VocabularyWord, VocabularyWordAsUsed, VocabularyWordGuesses)
                       VALUES (?,?,?,?,?,?,?,?)""",
                    (new_id, row["CreatedAt"], row["UpdatedAt"],
                     row["SentenceText"], row["RecommendedTranslation"],
                     row["VocabularyWord"], row["VocabularyWordAsUsed"],
                     row["VocabularyWordGuesses"]))
            self.stats["Challenge"]["inserted"] += 1
        print(f"   {self.stats['Challenge']}")

    # --- GradeResponse ---
    def _import_grade_responses(self):
        print("13. GradeResponse")
        bk_rows = self.bk.execute("SELECT * FROM GradeResponse").fetchall()

        for row in bk_rows:
            new_challenge_id = self.challenge_map.get(row["ChallengeID"])
            if not new_challenge_id:
                self.stats["GradeResponse"]["skipped"] += 1
                continue

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO GradeResponse 
                       (Fluency, FluencyExplanation, Accuracy, AccuracyExplanation,
                        RecommendedTranslation, CreatedAt, ChallengeID)
                       VALUES (?,?,?,?,?,?,?)""",
                    (row["Fluency"], row["FluencyExplanation"],
                     row["Accuracy"], row["AccuracyExplanation"],
                     row["RecommendedTranslation"], row["CreatedAt"],
                     new_challenge_id))
            self.stats["GradeResponse"]["inserted"] += 1
        print(f"   {self.stats['GradeResponse']}")

    # --- Conversation ---
    def _import_conversations(self):
        print("14. Conversation")
        bk_rows = self.bk.execute("SELECT * FROM Conversation").fetchall()

        for row in bk_rows:
            new_id = guid()
            self.conversation_map[row["Id"]] = new_id
            new_scenario_id = self.scenario_map.get(row["ScenarioId"])

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO Conversation (Id, CreatedAt, Language, ScenarioId)
                       VALUES (?,?,?,?)""",
                    (new_id, row["CreatedAt"], row["Language"], new_scenario_id))
            self.stats["Conversation"]["inserted"] += 1
        self.conversation_map[None] = None
        print(f"   {self.stats['Conversation']}")

    # --- ConversationChunk ---
    def _import_conversation_chunks(self):
        print("15. ConversationChunk")
        bk_rows = self.bk.execute("SELECT * FROM ConversationChunk").fetchall()

        for row in bk_rows:
            new_conv_id = self.conversation_map.get(row["ConversationId"])
            if not new_conv_id:
                self.stats["ConversationChunk"]["skipped"] += 1
                continue

            new_id = guid()
            self.chunk_map[row["Id"]] = new_id
            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO ConversationChunk 
                       (Id, SentTime, Author, ConversationId, Role,
                        GrammarCorrectionsJson, Text, Comprehension, ComprehensionNotes)
                       VALUES (?,?,?,?,?,?,?,?,?)""",
                    (new_id, row["SentTime"], row["Author"], new_conv_id,
                     row["Role"], row["GrammarCorrectionsJson"],
                     row["Text"], row["Comprehension"], row["ComprehensionNotes"]))
            self.stats["ConversationChunk"]["inserted"] += 1
        print(f"   {self.stats['ConversationChunk']}")

    # --- ConversationMemoryState ---
    def _import_conversation_memory_states(self):
        print("16. ConversationMemoryState")
        bk_rows = self.bk.execute("SELECT * FROM ConversationMemoryState").fetchall()

        for row in bk_rows:
            new_conv_id = self.conversation_map.get(row["ConversationId"])
            if not new_conv_id:
                self.stats["ConversationMemoryState"]["skipped"] += 1
                continue

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO ConversationMemoryState 
                       (ConversationId, SerializedState, ConversationSummary,
                        DiscussedVocabulary, DetectedProficiencyLevel,
                        CreatedAt, UpdatedAt)
                       VALUES (?,?,?,?,?,?,?)""",
                    (new_conv_id, row["SerializedState"],
                     row["ConversationSummary"], row["DiscussedVocabulary"],
                     row["DetectedProficiencyLevel"],
                     row["CreatedAt"], row["UpdatedAt"]))
            self.stats["ConversationMemoryState"]["inserted"] += 1
        print(f"   {self.stats['ConversationMemoryState']}")

    # --- UserActivity ---
    def _import_user_activities(self):
        print("17. UserActivity")
        bk_rows = self.bk.execute("SELECT * FROM UserActivity").fetchall()
        user_guid = self.user_map.get(1, "")

        # Dedup by CreatedAt + Activity
        existing = set()
        for r in self.tgt.execute(
            "SELECT Activity, CreatedAt FROM UserActivity"
        ).fetchall():
            existing.add((r["Activity"], r["CreatedAt"]))

        for row in bk_rows:
            if (row["Activity"], row["CreatedAt"]) in existing:
                self.stats["UserActivity"]["skipped"] += 1
                continue

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO UserActivity 
                       (Activity, Input, Fluency, Accuracy, UserProfileId, CreatedAt, UpdatedAt)
                       VALUES (?,?,?,?,?,?,?)""",
                    (row["Activity"], row["Input"], row["Fluency"],
                     row["Accuracy"], user_guid,
                     row["CreatedAt"], row["UpdatedAt"]))
            self.stats["UserActivity"]["inserted"] += 1
        print(f"   {self.stats['UserActivity']}")

    # --- DailyPlanCompletion ---
    def _import_daily_plan_completions(self):
        print("18. DailyPlanCompletion")
        bk_rows = self.bk.execute("SELECT * FROM DailyPlanCompletion").fetchall()

        # Dedup by Date + PlanItemId
        existing = set()
        for r in self.tgt.execute(
            "SELECT Date, PlanItemId FROM DailyPlanCompletion"
        ).fetchall():
            existing.add((r["Date"], r["PlanItemId"]))

        for row in bk_rows:
            if (row["Date"], row["PlanItemId"]) in existing:
                self.stats["DailyPlanCompletion"]["skipped"] += 1
                continue

            new_resource_id = self.resource_map.get(row["ResourceId"])
            new_skill_id = self.skill_map.get(row["SkillId"])

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO DailyPlanCompletion 
                       (Date, PlanItemId, ActivityType, ResourceId, SkillId,
                        IsCompleted, CompletedAt, MinutesSpent, EstimatedMinutes,
                        Priority, TitleKey, DescriptionKey, Rationale,
                        CreatedAt, UpdatedAt)
                       VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                    (row["Date"], row["PlanItemId"], row["ActivityType"],
                     new_resource_id, new_skill_id,
                     row["IsCompleted"], row["CompletedAt"],
                     row["MinutesSpent"], row["EstimatedMinutes"],
                     row["Priority"], row["TitleKey"], row["DescriptionKey"],
                     row["Rationale"], row["CreatedAt"], row["UpdatedAt"]))
            self.stats["DailyPlanCompletion"]["inserted"] += 1
        print(f"   {self.stats['DailyPlanCompletion']}")

    # --- StreamHistory ---
    def _import_stream_history(self):
        print("19. StreamHistory")
        bk_rows = self.bk.execute("SELECT * FROM StreamHistory").fetchall()

        existing = set()
        for r in self.tgt.execute(
            "SELECT Phrase, VoiceId FROM StreamHistory"
        ).fetchall():
            existing.add((r["Phrase"], r["VoiceId"]))

        for row in bk_rows:
            if (row["Phrase"], row["VoiceId"]) in existing:
                self.stats["StreamHistory"]["skipped"] += 1
                continue

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO StreamHistory 
                       (Phrase, CreatedAt, UpdatedAt, Duration, VoiceId,
                        AudioFilePath, FileName, Title, Source, SourceUrl)
                       VALUES (?,?,?,?,?,?,?,?,?,?)""",
                    (row["Phrase"], row["CreatedAt"], row["UpdatedAt"],
                     row["Duration"], row["VoiceId"],
                     row["AudioFilePath"], row["FileName"],
                     row["Title"], row["Source"], row["SourceUrl"]))
            self.stats["StreamHistory"]["inserted"] += 1
        print(f"   {self.stats['StreamHistory']}")

    # --- MinimalPair ---
    def _import_minimal_pairs(self):
        print("20. MinimalPair")
        bk_rows = self.bk.execute("SELECT * FROM MinimalPair").fetchall()
        user_guid = self.user_map.get(1, "")

        # Build existing set
        existing = set()
        for r in self.tgt.execute(
            "SELECT UserId, VocabularyWordAId, VocabularyWordBId FROM MinimalPair"
        ).fetchall():
            existing.add((r["UserId"], r["VocabularyWordAId"], r["VocabularyWordBId"]))

        # We need a map from old MinimalPair.Id → new MinimalPair.Id
        self.minimal_pair_map = {}

        for row in bk_rows:
            new_word_a = self.word_map.get(row["VocabularyWordAId"])
            new_word_b = self.word_map.get(row["VocabularyWordBId"])

            if not new_word_a or not new_word_b:
                self.stats["MinimalPair"]["skipped"] += 1
                continue

            if (user_guid, new_word_a, new_word_b) in existing:
                # Find existing ID
                eid = self.tgt.execute(
                    "SELECT Id FROM MinimalPair WHERE UserId=? AND VocabularyWordAId=? AND VocabularyWordBId=?",
                    (user_guid, new_word_a, new_word_b)
                ).fetchone()
                self.minimal_pair_map[row["Id"]] = eid["Id"] if eid else None
                self.stats["MinimalPair"]["skipped"] += 1
                continue

            if not self.dry_run:
                cur = self.tgt.execute(
                    """INSERT INTO MinimalPair 
                       (UserId, VocabularyWordAId, VocabularyWordBId,
                        ContrastLabel, CreatedAt, UpdatedAt)
                       VALUES (?,?,?,?,?,?)""",
                    (user_guid, new_word_a, new_word_b,
                     row["ContrastLabel"], row["CreatedAt"], row["UpdatedAt"]))
                self.minimal_pair_map[row["Id"]] = cur.lastrowid
            else:
                self.minimal_pair_map[row["Id"]] = row["Id"]  # placeholder
            self.stats["MinimalPair"]["inserted"] += 1
        print(f"   {self.stats['MinimalPair']}")

    # --- MinimalPairSession ---
    def _import_minimal_pair_sessions(self):
        print("21. MinimalPairSession")
        bk_rows = self.bk.execute("SELECT * FROM MinimalPairSession").fetchall()
        user_guid = self.user_map.get(1, "")

        self.mp_session_map = {}
        for row in bk_rows:
            if not self.dry_run:
                cur = self.tgt.execute(
                    """INSERT INTO MinimalPairSession 
                       (UserId, Mode, PlannedTrialCount, StartedAt, EndedAt, CreatedAt, UpdatedAt)
                       VALUES (?,?,?,?,?,?,?)""",
                    (user_guid, row["Mode"], row["PlannedTrialCount"],
                     row["StartedAt"], row["EndedAt"],
                     row["CreatedAt"], row["UpdatedAt"]))
                self.mp_session_map[row["Id"]] = cur.lastrowid
            else:
                self.mp_session_map[row["Id"]] = row["Id"]
            self.stats["MinimalPairSession"]["inserted"] += 1
        print(f"   {self.stats['MinimalPairSession']}")

    # --- MinimalPairAttempt ---
    def _import_minimal_pair_attempts(self):
        print("22. MinimalPairAttempt")
        bk_rows = self.bk.execute("SELECT * FROM MinimalPairAttempt").fetchall()
        user_guid = self.user_map.get(1, "")

        for row in bk_rows:
            new_session_id = self.mp_session_map.get(row["SessionId"])
            new_pair_id = self.minimal_pair_map.get(row["PairId"])
            new_prompt_id = self.word_map.get(row["PromptWordId"])
            new_selected_id = self.word_map.get(row["SelectedWordId"])

            if not all([new_session_id, new_pair_id, new_prompt_id, new_selected_id]):
                self.stats["MinimalPairAttempt"]["skipped"] += 1
                continue

            if not self.dry_run:
                self.tgt.execute(
                    """INSERT INTO MinimalPairAttempt 
                       (UserId, SessionId, PairId, PromptWordId, SelectedWordId,
                        IsCorrect, SequenceNumber, CreatedAt)
                       VALUES (?,?,?,?,?,?,?,?)""",
                    (user_guid, new_session_id, new_pair_id,
                     new_prompt_id, new_selected_id,
                     row["IsCorrect"], row["SequenceNumber"],
                     row["CreatedAt"]))
            self.stats["MinimalPairAttempt"]["inserted"] += 1
        print(f"   {self.stats['MinimalPairAttempt']}")

    # --- Validation ---
    def _validate(self):
        print("\n=== POST-IMPORT VALIDATION ===")
        errors = 0

        # Check FK integrity
        fk_checks = [
            ("ResourceVocabularyMapping", "ResourceId", "LearningResource", "Id"),
            ("ResourceVocabularyMapping", "VocabularyWordId", "VocabularyWord", "Id"),
            ("VocabularyProgress", "VocabularyWordId", "VocabularyWord", "Id"),
            ("VocabularyLearningContext", "VocabularyProgressId", "VocabularyProgress", "Id"),
            ("ConversationChunk", "ConversationId", "Conversation", "Id"),
            ("ExampleSentence", "VocabularyWordId", "VocabularyWord", "Id"),
            ("GradeResponse", "ChallengeID", "Challenge", "Id"),
        ]

        for child_table, child_col, parent_table, parent_col in fk_checks:
            orphans = self.tgt.execute(f"""
                SELECT COUNT(*) as cnt FROM "{child_table}" c
                WHERE c."{child_col}" IS NOT NULL
                AND c."{child_col}" NOT IN (SELECT "{parent_col}" FROM "{parent_table}")
            """).fetchone()["cnt"]
            if orphans > 0:
                print(f"   ⚠ {child_table}.{child_col} → {parent_table}: {orphans} orphans")
                errors += 1
            else:
                print(f"   ✓ {child_table}.{child_col} → {parent_table}: OK")

        if errors == 0:
            print("   All FK checks passed!")
        else:
            print(f"   {errors} FK issues found")

    # --- Summary ---
    def _print_summary(self):
        print("\n=== IMPORT SUMMARY ===")
        total_inserted = 0
        total_skipped = 0
        total_merged = 0
        for table in [
            "UserProfile", "ConversationScenario", "SceneImage",
            "SkillProfile", "LearningResource", "VocabularyWord",
            "VocabularyList", "ResourceVocabularyMapping",
            "VocabularyProgress", "VocabularyLearningContext",
            "ExampleSentence", "Challenge", "GradeResponse",
            "Conversation", "ConversationChunk", "ConversationMemoryState",
            "UserActivity", "DailyPlanCompletion", "StreamHistory",
            "MinimalPair", "MinimalPairSession", "MinimalPairAttempt",
        ]:
            s = self.stats[table]
            parts = []
            if s["inserted"]: parts.append(f"+{s['inserted']}")
            if s["skipped"]: parts.append(f"~{s['skipped']} skipped")
            if s["merged"]: parts.append(f"⇄{s['merged']} merged")
            total_inserted += s["inserted"]
            total_skipped += s["skipped"]
            total_merged += s["merged"]
            if parts:
                print(f"   {table:40s} {', '.join(parts)}")

        print(f"\n   TOTAL: +{total_inserted} inserted, ~{total_skipped} skipped, ⇄{total_merged} merged")


def main():
    parser = argparse.ArgumentParser(description="Import legacy SentenceStudio backup")
    parser.add_argument("--backup", default=os.path.expanduser("~/Library/sstudio.bk.db3"),
                        help="Path to backup database")
    parser.add_argument("--target", default=None,
                        help="Path to target database (default: server DB)")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--dry-run", action="store_true", help="Preview only, no writes")
    group.add_argument("--execute", action="store_true", help="Perform the import")
    args = parser.parse_args()

    if args.target is None:
        args.target = os.path.join(
            os.path.expanduser("~/Library/Application Support/sentencestudio/server"),
            "sentencestudio.db")

    importer = Importer(args.backup, args.target, dry_run=args.dry_run)
    success = importer.run()
    sys.exit(0 if success else 1)


if __name__ == "__main__":
    main()
