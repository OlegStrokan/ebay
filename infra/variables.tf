variable "aws_region" {
    type = string
    default = "eu-central-1"
}

variable "environment" {
    type = string
    default = "production"
}

variable "project_name" {
    type = string
    default = "strokanostan"
}

variable "eks_cluster_version" {
    type = string
    default = "1.30"
}

variable "eks_node_instance_types" {
    type = list(string)
    default = ["t3.xlarge"]
}

variable "eks_node_min" {
    type = number
    default = 2
}

variable "eks_node_max" {
    type = number
    default = 6
}

variable "eks_node_desired" {
    type = number
    default = 3
}

variable "vpc_cidr" {
    type    = string
    default = "10.0.0.0/16"
}

variable "eks_public_access_cidrs" {
    description = "CIDR blocks allowed to reach the EKS public API endpoint. Restrict to your VPN/office IP range. Set to [] and disable endpoint_public_access instead if no public access is needed."
    type        = list(string)
    # No default — must be set explicitly per environment to prevent accidental open access.
    # Example: ["203.0.113.0/24"]  (your VPN/bastion egress CIDR)
}

variable "db_username" {
    type = string
    default = "postgres"
    sensitive = true
}

variable "db_password" {
    type = string
    sensitive = true
}

variable "ecr_repositories" {
    type = list(string)
    default = [
        "free-ebay/gateway-api",
        "free-ebay/auth-service",
        "free-ebay/user-service",
        "free-ebay/inventory-service",
        "free-ebay/order-service",
        "free-ebay/product-service",
        "free-ebay/payment-service",
        "free-ebay/search-service",
        "free-ebay/catalog-service",
        "free-ebay/email-service",
        "free-ebay/ai-search-service",
        "free-ebay/embedding-service",
        "free-ebay/vector-indexer-worker"
    ]
}
