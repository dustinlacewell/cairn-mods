"""Archipelago world for Cairn, The Game Bakers' climbing game.

Locations are the game's numbered story-beat sensors (fired automatically as
the player climbs the route); items are the game's inventory items plus
Progressive Altitude Permits that gate each chapter of the mountain. The
game-side MelonLoader mod (CairnArchipelago) converts sensor triggers into
location checks and grants received items into the climber's inventory.
"""

from typing import Any

from BaseClasses import ItemClassification, Region, Tutorial
from worlds.AutoWorld import WebWorld, World

from .data import ITEM_BASE, LOCATION_BASE
from .items import (
    PROGRESSIVE_PERMIT,
    CairnItem,
    filler_item_names,
    item_classification,
    item_name_groups,
    item_name_to_id,
    useful_item_names,
)
from .locations import (
    CHAPTERS,
    FINAL_CHAPTER,
    SUMMIT_EVENT,
    CairnLocation,
    location_name_to_id,
    locations_by_chapter,
)
from .options import CairnOptions


class CairnWeb(WebWorld):
    tutorials = [
        Tutorial(
            "Multiworld Setup Guide",
            "A guide to setting up the Cairn randomizer for Archipelago multiworlds.",
            "English",
            "setup_en.md",
            "setup/en",
            ["ldlework"],
        )
    ]


class CairnWorld(World):
    """
    Cairn is a climbing game: you are Aava, an alpinist free-climbing the
    unconquered Mount Kami, managing gear, food and injuries on the way to
    the summit.
    """

    game = "Cairn"
    web = CairnWeb()
    options_dataclass = CairnOptions
    options: CairnOptions

    item_name_to_id = item_name_to_id
    location_name_to_id = location_name_to_id
    item_name_groups = item_name_groups

    def create_regions(self) -> None:
        menu = Region("Menu", self.player, self.multiworld)
        self.multiworld.regions.append(menu)

        previous = menu
        for chapter in CHAPTERS:
            region = Region(f"Chapter {chapter}", self.player, self.multiworld)
            region.add_locations(
                {name: location_name_to_id[name] for name in locations_by_chapter[chapter]},
                CairnLocation,
            )
            self.multiworld.regions.append(region)

            permits_needed = CHAPTERS.index(chapter)
            if permits_needed:
                previous.connect(
                    region,
                    f"Climb to Chapter {chapter}",
                    lambda state, n=permits_needed: state.has(
                        PROGRESSIVE_PERMIT, self.player, n
                    ),
                )
            else:
                previous.connect(region, f"Climb to Chapter {chapter}")
            previous = region

        summit = CairnLocation(self.player, SUMMIT_EVENT, None, previous)
        summit.place_locked_item(
            CairnItem(SUMMIT_EVENT, ItemClassification.progression, None, self.player)
        )
        previous.locations.append(summit)

    def create_item(self, name: str) -> CairnItem:
        return CairnItem(name, item_classification[name], item_name_to_id[name], self.player)

    def create_items(self) -> None:
        pool = [self.create_item(PROGRESSIVE_PERMIT) for _ in CHAPTERS[1:]]
        pool += [self.create_item(name) for name in useful_item_names]
        while len(pool) < len(self.multiworld.get_unfilled_locations(self.player)):
            pool.append(self.create_item(self.get_filler_item_name()))
        self.multiworld.itempool += pool

    def set_rules(self) -> None:
        self.multiworld.completion_condition[self.player] = lambda state: state.has(
            SUMMIT_EVENT, self.player
        )

    def get_filler_item_name(self) -> str:
        return self.random.choice(filler_item_names)

    def fill_slot_data(self) -> dict[str, Any]:
        goal_location = locations_by_chapter[FINAL_CHAPTER][-1]
        return {
            "death_link": bool(self.options.death_link),
            "item_base": ITEM_BASE,
            "location_base": LOCATION_BASE,
            "chapters": CHAPTERS,
            "goal_location": location_name_to_id[goal_location],
        }
