from BaseClasses import Location

from .data import LOCATION_BASE, STORY_SENSORS

SUMMIT_EVENT = "Summit"


class CairnLocation(Location):
    game = "Cairn"


# Locations map 1:1 onto the game's StoryEventSensorStringIdEnum; the client
# mod computes the AP id as (LOCATION_BASE + sensor enum value).
location_name_to_id = {
    display: LOCATION_BASE + value for display, _, value, _ in STORY_SENSORS
}


def chapter_of(zone: str) -> int:
    """Zones group into decade-chapters: 01/02 -> 0, 11..15 -> 1, ... 91..99 -> 9."""
    return int(zone[0])


# chapter -> [location display names], in route order (sensor code order).
locations_by_chapter: dict[int, list[str]] = {}
for display, _, _, zone in sorted(STORY_SENSORS, key=lambda row: row[0]):
    locations_by_chapter.setdefault(chapter_of(zone), []).append(display)

CHAPTERS = sorted(locations_by_chapter)
FINAL_CHAPTER = CHAPTERS[-1]
