CREATE DATABASE IF NOT EXISTS `wechat_monitor`
CHARACTER SET utf8mb4
COLLATE utf8mb4_unicode_ci;

USE `wechat_monitor`;

CREATE TABLE IF NOT EXISTS `messages` (
  `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `wxid` VARCHAR(255) NOT NULL,
  `nickname` VARCHAR(255) NOT NULL DEFAULT '',
  `sender` VARCHAR(255) NOT NULL DEFAULT '',
  `content` TEXT NOT NULL,
  `content_hash` CHAR(64) NOT NULL,
  `create_time` BIGINT NOT NULL,
  `is_sender` TINYINT(1) NOT NULL DEFAULT 0,
  `avatar` LONGTEXT NULL,
  `msg_type` INT NOT NULL DEFAULT 0,
  `msg_sub_type` INT NOT NULL DEFAULT 0,
  `media_type` VARCHAR(32) NOT NULL DEFAULT '',
  `media_mime` VARCHAR(80) NOT NULL DEFAULT '',
  `media_name` VARCHAR(255) NOT NULL DEFAULT '',
  `media_data` LONGTEXT NULL,
  `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uniq_message` (`wxid`, `create_time`, `content_hash`),
  KEY `idx_messages_create_time` (`create_time`),
  KEY `idx_messages_nickname` (`nickname`),
  KEY `idx_messages_wxid` (`wxid`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `contacts` (
  `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `wxid` VARCHAR(255) NOT NULL,
  `alias` VARCHAR(255) NOT NULL DEFAULT '',
  `remark` VARCHAR(255) NOT NULL DEFAULT '',
  `nick_name` VARCHAR(255) NOT NULL DEFAULT '',
  `display_name` VARCHAR(255) NOT NULL DEFAULT '',
  `avatar` LONGTEXT NULL,
  `source_updated_at` BIGINT NOT NULL DEFAULT 0,
  `extra_json` JSON NULL,
  `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uniq_contact_wxid` (`wxid`),
  KEY `idx_contacts_display_name` (`display_name`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `favorites` (
  `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `source_table` VARCHAR(255) NOT NULL DEFAULT '',
  `source_id` VARCHAR(255) NOT NULL DEFAULT '',
  `title` VARCHAR(255) NOT NULL DEFAULT '',
  `summary` TEXT NULL,
  `item_type` VARCHAR(80) NOT NULL DEFAULT '',
  `item_sub_type` VARCHAR(80) NOT NULL DEFAULT '',
  `source_updated_at` BIGINT NOT NULL DEFAULT 0,
  `data_json` JSON NULL,
  `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uniq_favorite_item` (`source_table`, `source_id`),
  KEY `idx_favorites_updated_at` (`source_updated_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `monitor_status` (
  `id` TINYINT NOT NULL,
  `last_heartbeat` BIGINT NOT NULL DEFAULT 0,
  `decrypt_ok` TINYINT(1) NOT NULL DEFAULT 0,
  `wechat_logged_in` TINYINT(1) NOT NULL DEFAULT 0,
  `updated_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `monitor_errors` (
  `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `message` TEXT NOT NULL,
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `event_logs` (
  `id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  `event_name` VARCHAR(120) NOT NULL,
  `source` VARCHAR(32) NOT NULL,
  `session_id` VARCHAR(120) NOT NULL DEFAULT '',
  `payload_json` JSON NULL,
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_event_name` (`event_name`),
  KEY `idx_event_source` (`source`),
  KEY `idx_event_created_at` (`created_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO `monitor_status` (`id`, `last_heartbeat`, `decrypt_ok`)
VALUES (1, 0, 0)
ON DUPLICATE KEY UPDATE `id` = `id`;
