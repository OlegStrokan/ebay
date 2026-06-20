resource "aws_eks_cluster" "main" {
    name = "${var.project_name}-cluster"
    version = var.eks_cluster_version
    role_arn = aws_iam_role.eks_cluster.arn

    vpc_config {
        subnet_ids = aws_subnet.private[*].id
        security_group_ids = [aws_security_group.eks_cluster.id]
        endpoint_private_access = true
        endpoint_public_access  = true
        public_access_cidrs     = var.eks_public_access_cidrs
    }

    enabled_cluster_log_types = ["api", "audit", "authenticator"]

    depends_on = [
        aws_iam_role_policy_attachment.eks_cluster_policy,
        aws_cloudwatch_log_group.eks,
    ]

    tags = {
        Name = "${var.project_name}-cluster"
        Environment = var.environment
    }
}

resource "aws_cloudwatch_log_group" "eks" {
    name = "/aws/eks/${var.project_name}-cluster/cluster"
    retention_in_days = 7
}

resource "aws_eks_node_group" "main" {
    cluster_name = aws_eks_cluster.main.name
    node_group_name = "${var.project_name}-nodes"
    node_role_arn = aws_iam_role.eks_node.arn
    subnet_ids = aws_subnet.private[*].id
    instance_types = var.eks_node_instance_types

    scaling_config {
        desired_size = var.eks_node_desired
        max_size = var.eks_node_max
        min_size = var.eks_node_min
    }

    update_config {
        max_unavailable = 1
    }

    labels = {
        role = "worker"
        environment = var.environment
    }

    depends_on = [
        aws_iam_role_policy_attachment.eks_node_policy,
        aws_iam_role_policy_attachment.eks_cni_policy,
        aws_iam_role_policy_attachment.eks_ecr_policy,
    ]

    tags = {
        Name = "${var.project_name}-node-group"
        Environment = var.environment
    }
}

resource "aws_eks_addon" "ebs_csi" {
    cluster_name = aws_eks_cluster.main.name
    addon_name = "aws-ebs-csi-driver"
    service_account_role_arn = aws_iam_role.ebs_csi.arn
    resolve_conflicts_on_create = "OVERWRITE"
}

resource "aws_eks_addon" "coredns" {
    cluster_name = aws_eks_cluster.main.name
    addon_name = "coredns"
    resolve_conflicts_on_create = "OVERWRITE"
    depends_on = [aws_eks_node_group.main]
}

resource "aws_eks_addon" "kube_proxy" {
    cluster_name = aws_eks_cluster.main.name
    addon_name = "kube-proxy"
    resolve_conflicts_on_create = "OVERWRITE"
}

resource "aws_eks_addon" "vpc_cni" {
    cluster_name = aws_eks_cluster.main.name
    addon_name = "vpc-cni"
    resolve_conflicts_on_create = "OVERWRITE"
}

resource "local_file" "storageclass" {
    filename = "${path.module}/../k8s/storage-class.yaml"
    content = <<-YAML
    apiVersion: storage.k8s.io/v1
    kind: StorageClass
    metadata:
      name: gp3
      annotations:
        storageclass.kubernetes.io/is-default-class: "true"
    provisioner: ebs.csi.aws.com
    volumeBindingMode: WaitForFirstConsumer
    parameters:
      type: gp3
      fsType: ext4
      encrypted: "true"
    YAML
}

resource "aws_security_group" "eks_cluster" {
    name = "${var.project_name}-eks-cluster-sg"
    description = "EKS cluster control plane communication"
    vpc_id = aws_vpc.main.id

    egress {
        from_port = 0
        to_port = 0
        protocol = "-1"
        cidr_blocks = ["0.0.0.0/0"]
    }

    tags = { Name = "${var.project_name}-eks-cluster-sg" }
}

resource "aws_iam_role" "eks_cluster" {
    name = "${var.project_name}-eks-cluster-role"

    assume_role_policy = jsonencode({
        Version = "2012-10-17"
        Statement = [{
            Effect = "Allow"
            Principal = { Service = "eks.amazonaws.com" }
            Action = "sts:AssumeRole"
        }]
    })
}

resource "aws_iam_role_policy_attachment" "eks_cluster_policy" {
    policy_arn = "arn:aws:iam::aws:policy/AmazonEKSClusterPolicy"
    role = aws_iam_role.eks_cluster.name
}

resource "aws_iam_role" "eks_node" {
    name = "${var.project_name}-eks-node-role"

    assume_role_policy = jsonencode({
        Version = "2012-10-17"
        Statement = [{
            Effect = "Allow"
            Principal = { Service = "ec2.amazonaws.com" }
            Action = "sts:AssumeRole"
        }]
    })
}

resource "aws_iam_role_policy_attachment" "eks_node_policy" {
    policy_arn = "arn:aws:iam::aws:policy/AmazonEKSWorkerNodePolicy"
    role = aws_iam_role.eks_node.name
}

resource "aws_iam_role_policy_attachment" "eks_cni_policy" {
    policy_arn = "arn:aws:iam::aws:policy/AmazonEKS_CNI_Policy"
    role = aws_iam_role.eks_node.name
}

resource "aws_iam_role_policy_attachment" "eks_ecr_policy" {
    policy_arn = "arn:aws:iam::aws:policy/AmazonEC2ContainerRegistryReadOnly"
    role = aws_iam_role.eks_node.name
}

data "aws_caller_identity" "current" {}

resource "aws_iam_role" "ebs_csi" {
    name = "${var.project_name}-ebs-csi-role"

    assume_role_policy = jsonencode({
        Version = "2012-10-17"
        Statement = [{
            Effect = "Allow"
            Principal = {
                Federated = "arn:aws:iam::${data.aws_caller_identity.current.account_id}:oidc-provider/${replace(aws_eks_cluster.main.identity[0].oidc[0].issuer, "https://", "")}"
            }
            Action = "sts:AssumeRoleWithWebIdentity"
            Condition = {
                StringEquals = {
                    "${replace(aws_eks_cluster.main.identity[0].oidc[0].issuer, "https://", "")}:sub" = "system:serviceaccount:kube-system:ebs-csi-controller-sa"
                }
            }
        }]
    })
}

resource "aws_iam_role_policy_attachment" "ebs_csi_policy" {
    policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonEBSCSIDriverPolicy"
    role = aws_iam_role.ebs_csi.name
}

resource "aws_iam_openid_connect_provider" "eks" {
    client_id_list = ["sts.amazonaws.com"]
    thumbprint_list = [data.tls_certificate.eks.certificates[0].sha1_fingerprint]
    url = aws_eks_cluster.main.identity[0].oidc[0].issuer
}

data "tls_certificate" "eks" {
    url = aws_eks_cluster.main.identity[0].oidc[0].issuer
}
